using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Akka.Actor;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Morgana.AI.Attributes;
using Morgana.AI.Interfaces;
using Morgana.AI.Providers;
using Morgana.Contracts;
using Status = Akka.Actor.Status;

namespace Morgana.AI.Abstractions;

/// <summary>
/// Base class for domain-specific conversational agents in the Morgana framework.
/// Extends <see cref="MorganaActor"/> with AI agent capabilities, session management,
/// conversation context and inter-agent communication.
/// </summary>
public class MorganaAgent : MorganaActor
{
    /// <summary>
    /// Underlying Microsoft.Agents.AI agent driving LLM interactions for this actor.
    /// Assigned by the concrete agent's constructor, together with the two providers, through
    /// <c>MorganaAgentAdapter.CreateAgent</c> and reused across turns.
    /// </summary>
    protected AIAgent aiAgent;

    /// <summary>
    /// Active <see cref="AgentSession"/> for the current conversation.
    /// Loaded from persistence on the first turn, then mutated in place across turns.
    /// Null before the first <see cref="ExecuteAgentAsync"/> call.
    /// </summary>
    protected AgentSession? aiAgentSession;

    /// <summary>
    /// The throwaway session a consultation is answered on, non-null only for that turn.
    /// </summary>
    private AgentSession? consultationSession;

    /// <summary>
    /// The active <see cref="AgentSession"/> for the current turn.
    /// Exposed so that tool closures can pass it to provider calls (GetVariable, SetVariable, etc.).
    /// Always non-null during a live agent invocation.
    /// </summary>
    public AgentSession? CurrentSession => consultationSession ?? aiAgentSession;

    /// <summary>
    /// Provider holding per-session variables and the shared-context write callback that
    /// persists shared variables into the conversation-scoped registry. Tools read and write
    /// through this provider.
    /// </summary>
    protected MorganaAIContextProvider aiContextProvider;

    /// <summary>
    /// Provider exposing the chat history of the current <see cref="aiAgentSession"/>.
    /// Consulted by tools that need conversation context (e.g. summarization, citations).
    /// </summary>
    protected MorganaChatHistoryProvider aiChatHistoryProvider;

    /// <summary>
    /// Persistence service used to load, save and resume serialized <see cref="AgentSession"/>s
    /// across actor restarts and conversation resumes.
    /// </summary>
    protected readonly IConversationPersistenceService persistenceService;

    /// <summary>
    /// Logger scoped to this agent instance, used for turn-level diagnostics and tool tracing.
    /// </summary>
    protected readonly ILogger agentLogger;

    /// <summary>
    /// Intent name handled by this agent, resolved from the mandatory
    /// <see cref="HandlesIntentAttribute"/> on the concrete subclass.
    /// </summary>
    protected string AgentIntent
        => GetType().GetCustomAttribute<HandlesIntentAttribute>()?.Intent
            ?? throw new InvalidOperationException($"Agent {GetType().Name} must be decorated with [HandlesIntent] attribute");

    /// <summary>
    /// Stable identifier for this agent within a conversation, formatted as
    /// <c>{AgentIntent}-{conversationId}</c>. Used as the persistence key for <see cref="AgentSession"/>.
    /// </summary>
    protected string AgentIdentifier
        => $"{AgentIntent}-{conversationId}";

    /// <summary>
    /// Initializes the agent actor and wires the three messages it answers.
    /// </summary>
    /// <param name="conversationId">Conversation this agent is scoped to.</param>
    /// <param name="llmService">LLM service used by the underlying <see cref="AIAgent"/>.</param>
    /// <param name="promptResolverService">Resolver for framework + domain prompts.</param>
    /// <param name="persistenceService">Persistence service used to load/save <see cref="AgentSession"/>.</param>
    /// <param name="agentLogger">Logger for the concrete agent subclass.</param>
    /// <param name="configuration">Application configuration (streaming flags, etc.).</param>
    public MorganaAgent(
        string conversationId,
        ILanguageModelService llmService,
        IPromptResolverService promptResolverService,
        IConversationPersistenceService persistenceService,
        ILogger agentLogger,
        IConfiguration configuration) : base(conversationId, llmService, promptResolverService, configuration)
    {
        // Persistence and logger are the agent's own; the base class already holds everything an actor shares.
        this.persistenceService = persistenceService;
        this.agentLogger = agentLogger;

        // The user's turn, dispatched by the router once the supervisor has routed the intent here.
        ReceiveAsync<Records.AgentRequest>(ExecuteAgentAsync);

        // A colleague's question, arriving from MorganaHostedAgent over A2A. It never passes through
        // the supervisor, so it is the one turn this actor runs outside the conversation's pipeline.
        ReceiveAsync<Records.PeerConsultation>(ServeConsultationAsync);

        // A turn that threw, sent to Self so the answer to the waiting sender is composed on the
        // actor's own thread rather than inside the catch that could not produce one.
        ReceiveAsync<Records.FailureContext>(HandleAgentFailureAsync);
    }

    /// <summary>
    /// Restores a serialized <see cref="AgentSession"/>, including conversation history and context state.
    /// After loading, reconnects the <see cref="MorganaAIContextProvider.OnSharedContextUpdate"/> callback
    /// so subsequent shared-variable writes from this session land in the conversation-scoped
    /// <c>shared_context</c> registry.
    /// </summary>
    public virtual async Task<AgentSession> DeserializeSessionAsync(
        JsonElement serializedState,
        JsonSerializerOptions? jsonSerializerOptions = null)
    {
        // What comes back is the agent's own session from here on: the callback rewired below and
        // every later turn, act on this instance rather than on whatever the actor held before.
        aiAgentSession = await aiAgent.DeserializeSessionAsync(serializedState, jsonSerializerOptions);

        // Reconnect the shared-write callback — delegates are not serialized.
        aiContextProvider.OnSharedContextUpdate = OnSharedContextUpdate;

        agentLogger.LogInformation("Deserialized AgentSession for conversation {ConversationId}", conversationId);

        // The caller receives the same instance that the actor holds, so both act on one session.
        return aiAgentSession;
    }

    /// <summary>
    /// Callback invoked by <see cref="MorganaAIContextProvider"/> when a shared context variable
    /// is set. Persists the variable to the conversation-scoped <c>shared_context</c> registry so
    /// that any agent in the conversation — alive, dormant, dead-and-rehydrated or never yet
    /// activated — can pick it up at the start of its next turn via
    /// <see cref="IConversationPersistenceService.LoadSharedVariablesAsync"/>.
    /// </summary>
    /// <param name="key">Name of the shared variable.</param>
    /// <param name="value">Value to persist.</param>
    protected async Task OnSharedContextUpdate(string key, object value)
    {
        // The write of a turn served for a colleague dies with the ephemeral session it was made on,
        // exactly like everything else that turn wrote. Were it let through, first-write-wins would
        // make a value nobody ever confirmed to the user binding on every agent for the rest of the
        // conversation — an exchange that leaves no trace legislating for the whole shop.
        if (consultationSession is not null)
        {
            agentLogger.LogInformation("Agent {AgentIntent} is serving a consultation: shared context variable '{Key}' stays local to the exchange", AgentIntent, key);
            return;
        }

        agentLogger.LogInformation("Agent {AgentIntent} writing shared context variable: {Key}", AgentIntent, key);

        // Awaited by the tool call that wrote the variable, itself running inside the agent's
        // ReceiveAsync: the persisted write lands before the turn issues its next tool call,
        // preserving first-write-wins ordering without blocking the actor thread.
        await persistenceService.UpsertSharedVariableAsync(conversationId, key, value, AgentIntent);
    }

    /// <summary>
    /// Processes an incoming <see cref="Records.AgentRequest"/>, running the LLM turn
    /// and streaming or batching the response back to the sender.
    /// </summary>
    /// <param name="req">Agent request containing the user's message, optional classification and OTel TurnContext</param>
    protected virtual async Task ExecuteAgentAsync(Records.AgentRequest req)
    {
        // The sender is captured before the first await: it is the router waiting for this turn's answer
        // and the actor's Sender does not name it once the turn has yielded.
        IActorRef? senderRef = Sender;

        // Open morgana.agent span as child of the turn span propagated from the supervisor.
        // The span stays open for the full duration of LLM streaming so TTFT can be recorded.
        Activity? agentSpan = Telemetry.Source.StartActivity(
            Telemetry.AgentActivity,
            ActivityKind.Internal,
            req.TurnContext);

        // The tags let a trace be found by conversation and by the agent that served the turn.
        agentSpan?.SetTag(Telemetry.ConversationId, conversationId);
        agentSpan?.SetTag(Telemetry.AgentName, GetType().Name);
        agentSpan?.SetTag(Telemetry.AgentIntent, AgentIntent);

        try
        {
            // The buttons that let the user stay, leave or approve are the framework's, worded in morgana.json.
            Records.Prompt morganaPrompt = await promptResolverService.ResolveAsync(Constants.Morgana);
            Records.FrameworkReplies frameworkReplies = Records.FrameworkReplies.From(
                morganaPrompt.GetAdditionalPropertyOrDefault<List<Records.FrameworkReplySet>>(Constants.PromptProperties.FrameworkReplies, []));

            // A press of the button to stay or of the one to leave settles how the turn closes, whatever the model answers.
            QuickReply? pressedFrameworkReply = frameworkReplies.Closure.Concat(frameworkReplies.Escape)
                .FirstOrDefault(button => string.Equals(button.Value, req.Content?.Trim(), StringComparison.OrdinalIgnoreCase));

            // The turn is filed before the model runs, then run, then closed and recorded.
            Records.TurnOpening turnOpening = await OpenTurnAsync(req, frameworkReplies, pressedFrameworkReply, agentSpan);
            Records.TurnOutcome turnOutcome = await RunTurnAsync(turnOpening.Session, req, turnOpening.PressedCall?.Tool, senderRef, agentSpan);
            Records.TurnClosure turnClosure = ConcludeTurn(turnOpening.Session, turnOutcome, morganaPrompt, frameworkReplies, pressedFrameworkReply, req.IsLanding);
            DateTime? recordedAt = await RecordTurnAsync(turnOpening.Session, turnClosure, turnOpening.HistoryBaseline, agentSpan);

            // Dated as the history keeps it, so a client catching up recognises the reply it was pushed.
            senderRef.Tell(new Records.AgentResponse(turnClosure.Text, turnClosure.IsCompleted, turnClosure.QuickReplies, turnClosure.Card, recordedAt));
        }
        // The provider of the agent's tier decides whether the failure is its own content refusal; the registry
        // refuses at startup an agent without [RequiresLLMTier], so the tier is always declared.
        catch (Exception ex) when (llmService.GetLanguageModel(GetType().GetCustomAttribute<RequiresLLMTierAttribute>()!.Tier).IsContentFilterRefusal(ex))
        {
            // The provider refused the content itself: the user is told so by the supervisor and no retry would pass.
            agentLogger.LogWarning(ex, "Content filter rejection in {Name} for conversation {ConversationId}", GetType().Name, conversationId);
            agentSpan?.SetStatus(ActivityStatusCode.Error, "content_filter");
            agentSpan?.AddException(ex);
            agentSpan?.Dispose();

            // The supervisor is told of the refusal, so the user is answered as a guard rejection.
            senderRef.Tell(new Records.ContentFilterRejection());
        }
        catch (Exception ex)
        {
            // The failure is handed to the actor's own mailbox: the waiting sender is answered from a handler that can await.
            agentLogger.LogError(ex, "Error in {Name}", GetType().Name);
            agentSpan?.SetStatus(ActivityStatusCode.Error, ex.Message);
            agentSpan?.AddException(ex);
            agentSpan?.Dispose();

            // The failure goes to the actor's own mailbox, which answers the waiting sender from a handler that can await.
            Self.Tell(new Records.FailureContext(new Status.Failure(ex), senderRef));
        }
        finally
        {
            // Safety net: a turn's closure and its consultation budget must NEVER leak to the next turn.
            if (aiAgentSession is not null)
            {
                // The turn's closure is dropped from the session, so it cannot close the next turn as well.
                aiContextProvider.DropVariable(aiAgentSession, Constants.ContextKeys.ClosingDeclaration);
                aiContextProvider.DropVariable(aiAgentSession, Constants.ContextKeys.Landing);
                aiContextProvider.DropVariable(aiAgentSession, Constants.ContextKeys.RunInstructions);
                aiContextProvider.DropVariable(aiAgentSession, Constants.ContextKeys.PressedCallMade);
                aiContextProvider.DropVariable(aiAgentSession, Constants.ContextKeys.ConsultationRounds);
            }
        }
    }

    /// <summary>Files the user's phrase in the agent's session and the record before the model reads it.</summary>
    private async Task<Records.TurnOpening> OpenTurnAsync(Records.AgentRequest req, Records.FrameworkReplies frameworkReplies, QuickReply? pressedFrameworkReply, Activity? agentSpan)
    {
        // Read from the database on first activation or when the row was rewritten since this actor read it.
        // Otherwise the field holds the live session, which is exactly what the row says at a turn's start.
        // The agent hands itself over because deserializing a session is its own responsibility.
        if (aiAgentSession is null || await persistenceService.IsDirtyAsync(AgentIdentifier))
        {
            // The session is read from the record, which a rewrite behind the agent may have changed.
            aiAgentSession = await persistenceService.LoadAgentConversationAsync(AgentIdentifier, this);

            // Reported only when the record was actually read: a turn served from the live session resumes nothing
            if (aiAgentSession != null)
            {
                agentLogger.LogInformation("Loaded existing conversation session for {AgentIdentifier}", AgentIdentifier);

                // The trace records that an existing conversation was resumed.
                agentSpan?.AddEvent(new ActivityEvent(Telemetry.ResumeAgentConversation));
            }
        }
        // Without a session on record, the turn starts a new one.
        if (aiAgentSession is null)
        {
            // No row under this identifier: first time this agent is activated in the conversation.
            // It starts with an empty history — the shared registry below is all it inherits.
            aiAgentSession = await aiAgent.CreateSessionAsync();

            agentLogger.LogInformation("Created new conversation session for {AgentIdentifier}", AgentIdentifier);

            // The trace records that a new conversation began.
            agentSpan?.AddEvent(new ActivityEvent(Telemetry.CreateAgentConversation));
        }

        // Tagged after the session exists, so a trace names the row that the turn was served from.
        agentSpan?.SetTag(Telemetry.AgentIdentifier, AgentIdentifier);

        // Hydrate the agent's local context from the conversation-scoped shared_context
        // registry. Shared variables produced by any other agent of this conversation —
        // whether currently alive, dormant, dead-and-rehydrated or never yet activated —
        // are stored centrally in the per-conversation DB and pulled here at turn start.
        // First-write-wins is enforced at two levels:
        //   1. Storage layer: UpsertSharedVariableAsync uses INSERT OR IGNORE, so once a
        //      variable name has a value it cannot be replaced by a later writer.
        //   2. Local merge: MergeSharedContext skips variables already present in this
        //      agent's own session, so an agent that has set its own value never sees it
        //      overwritten by a registry entry.
        Dictionary<string, object> sharedFromRegistry = await persistenceService.LoadSharedVariablesAsync(conversationId);
        if (sharedFromRegistry.Count > 0)
        {
            agentLogger.LogInformation(
                "Agent '{AgentIntent}' hydrating {Count} shared variable(s) from registry: {Keys}",
                AgentIntent, sharedFromRegistry.Count, string.Join(", ", sharedFromRegistry.Keys));

            // Merged into the agent's own session, so the values survive the turn and are persisted
            // with it — unlike a consultation, which merges the same registry into a session that dies.
            aiContextProvider.MergeSharedContext(aiAgentSession, sharedFromRegistry);
        }

        // A tool still waiting for the user's approval is answered by this very message: approved only
        // when it is the approval button, declined by anything else, which then reads as an ordinary
        // request. Read from the stored history, so a turn resumed after a restart is answered alike.
        List<AIContent> userContents = [new TextContent(req.Content!)];
        List<ToolApprovalRequestContent> pendingApprovals = GetPendingApprovals(aiAgentSession);
        if (pendingApprovals.Count > 0)
        {
            // The user's answer approves the pending calls only when it is the approve button's own text.
            bool approved = frameworkReplies.Approval is [QuickReply approveButton, ..]
                              && string.Equals(req.Content?.Trim(), approveButton.Value, StringComparison.OrdinalIgnoreCase);
            userContents.AddRange(pendingApprovals.Select(AIContent (request) => request.CreateResponse(approved)));

            agentLogger.LogInformation(
                "Agent '{AgentIntent}' {Outcome} {Count} tool call(s) on the user's answer",
                AgentIntent, approved ? "runs the approved" : "declines", pendingApprovals.Count);
        }

        // Dated here because the history is merged chronologically across agents.
        ChatMessage userMessage = new ChatMessage(ChatRole.User, userContents) { CreatedAt = DateTimeOffset.UtcNow };

        // The call that this message commits the turn to: the press of an action that the last turn delivered or, failing
        // that, an earlier press still waiting for the user's typed answer. A landing opens a stay with the agent and commits
        // it to nothing, as the press of a button to stay or leave does: offered beside a question, it lets go of the call
        // still waiting for a typed value. The press is already the user's consent to the call.
        Records.PressedCall? pressedCall = req.IsLanding || pressedFrameworkReply is not null ? null : PressedCallOf(aiAgentSession, req.Content);

        // A landing is the one turn in which the agent can present itself: the flag is set before the model
        // runs and dropped with the turn.
        if (req.IsLanding)
        {
            // The landing mark is what offers the model Reply alone, so this turn introduces the agent and calls none of its tools.
            await aiContextProvider.SetVariableAsync(aiAgentSession, Constants.ContextKeys.Landing, true);

            // The landing run carries the declaration of the agent's menu, where the deployment declares one.
            if (aiContextProvider.LandingDeclaration is not null)
                await aiContextProvider.SetVariableAsync(aiAgentSession, Constants.ContextKeys.RunInstructions, aiContextProvider.LandingDeclaration);
        }

        // The model clients read the pressed call before the model runs: the framework makes it or the model composes it alone.
        // Saved with the phrase below, a press survives a restart; any earlier press that this message does not carry is let go.
        if (pressedCall is null)
            aiContextProvider.DropPressedCall(aiAgentSession);
        else
            aiContextProvider.SetPressedCall(aiAgentSession, pressedCall);

        // A phrase that arrived while no agent was active is already saved as Morgana's own.
        // This agent keeps it because its model has to read it, marked as somebody else's so a
        // transcript shows the user their own words once.
        if (req.ContentAlreadyStored)
        {
            // The phrase is already on record as Morgana's, so this copy is marked as meant for the model alone.
            userMessage.AdditionalProperties ??= new AdditionalPropertiesDictionary();
            userMessage.AdditionalProperties[Constants.MessageProperties.ContextOnly] = true;
        }

        // History length before this turn runs: everything appended past this point belongs to
        // the turn, which is what agent.tools_invoked must report — the span is per-turn, so
        // reporting the whole session would attribute every past tool call to this one.
        int historyBaseline = aiChatHistoryProvider.GetMessages(aiAgentSession).Count;

        // The phrase enters the session before a single token is spent on it. The turn below runs
        // on the session alone, so nothing reaches the model twice. Written out too, because
        // when this agent was already active nobody else saved the phrase: its own row is the
        // only record. A client reloading mid-turn reads it back from here instead of
        // watching it vanish. The row is left active on purpose: a turn interrupted here resumes
        // at the agent that was working on it.
        aiChatHistoryProvider.AppendMessage(aiAgentSession, userMessage);
        await persistenceService.SaveAgentConversationAsync(AgentIdentifier, aiAgent, aiAgentSession, isCompleted: false);

        // The opened turn hands back its session, the history baseline and any pressed call.
        return new Records.TurnOpening(aiAgentSession, historyBaseline, pressedCall);
    }

    /// <summary>Runs the model over the session until the turn has an answer recorded by Reply or awaits the user's approval.</summary>
    private async Task<Records.TurnOutcome> RunTurnAsync(AgentSession session, Records.AgentRequest req, string? pressedActionTool, IActorRef senderRef, Activity? agentSpan)
    {
        // Streaming is gated on two independent signals:
        //   1. Global config flag (Morgana:AdaptiveMessaging:EnableStreamingResponse)
        //   2. Channel capability — we don't even attach to the LLM streaming endpoint
        //      when the outbound channel can't deliver chunks to the user. When Capabilities
        //      is null (legacy/test paths) we assume the channel supports streaming.
        // Both signals are read once per turn: a channel cannot change its capabilities mid-turn.
        bool streamingEnabled = configuration.GetValue("Morgana:AdaptiveMessaging:EnableStreamingResponse", true);
        bool channelSupportsStreaming = req.Capabilities?.SupportsStreaming ?? true;
        bool useStreaming = streamingEnabled && channelSupportsStreaming;

        // Said once per turn so that a batched answer on a streaming-capable deployment is explained.
        if (!channelSupportsStreaming)
            agentLogger.LogInformation("Agent '{AgentIntent}' bypassing LLM streaming: channel does not advertise SupportsStreaming", AgentIntent);

        // The words of the turn reach the channel once: the moment Reply is accepted, whichever pass accepts it.
        bool textStreamed = false;

        // One pass of the model over the session: the answer is recorded by Reply, so nothing is assembled here.
        async Task RunModelAsync()
        {
            // Streaming is used only when both signals allow it and otherwise the whole answer arrives in one piece.
            if (useStreaming)
            {
                // Time to first token is what the user waits before seeing the agent answer.
                Stopwatch firstChunkStopwatch = Stopwatch.StartNew();

                // The whole turn runs here — tool calls included, which surface as chunks carrying no text.
                // The phrase being answered is the one already filed above, so nothing is handed in here.
                await foreach (AgentResponseUpdate chunk in aiAgent.RunStreamingAsync(session))
                {
                    // The text reaches the channel the moment Reply is accepted: free text the model writes is never shown.
                    if (!textStreamed && GetClosingDeclarationFromContext(session) is { } acceptedReply && !string.IsNullOrWhiteSpace(acceptedReply.Text))
                    {
                        // The turn's words travel as one chunk and are never streamed twice.
                        textStreamed = true;
                        senderRef.Tell(new Records.AgentStreamChunk(acceptedReply.Text));

                        // The accepted Reply fixes the time to first token, which is measured once per turn.
                        long ttft = firstChunkStopwatch.ElapsedMilliseconds;
                        firstChunkStopwatch.Stop();
                        agentSpan?.AddEvent(new ActivityEvent(Telemetry.EventFirstChunk));
                        agentSpan?.SetTag(Telemetry.AgentTtftMs, ttft);
                        Telemetry.AgentTtftHistogram.Record(ttft);
                    }
                    else
                    {
                        // The turn is advancing on something that is not the answer — a tool being called
                        // or a colleague being asked. The supervisor's wait counts silence and a
                        // consultation is a whole turn at another agent: unannounced, it reads as an
                        // agent that has died and the user's turn is abandoned while its answer is
                        // still being written.
                        senderRef.Tell(new Records.AgentStillWorking());
                    }
                }
            }
            else
            {
                // Without streaming the user waits for the whole answer, which is what the span records as first token.
                Stopwatch responseStopwatch = Stopwatch.StartNew();
                // Same turn as the streaming branch, answering the same already-filed phrase: nothing
                // reaches the channel until the model and every tool it decided to call, are done.
                await aiAgent.RunAsync(session);
                responseStopwatch.Stop();

                // The batched answer reaches the user as one piece, so its latency is the time to first token.
                long ttft = responseStopwatch.ElapsedMilliseconds;
                agentSpan?.AddEvent(new ActivityEvent(Telemetry.EventFirstChunk));
                agentSpan?.SetTag(Telemetry.AgentTtftMs, ttft);
                Telemetry.AgentTtftHistogram.Record(ttft);
            }
        }

        // The first pass answers the user's phrase.
        await RunModelAsync();

        // A call that needs approval, to the very tool that a pressed button committed this turn to, was approved by
        // that press: it runs now and the user is never asked to confirm the same thing twice.
        List<ToolApprovalRequestContent> approvedByPress = [.. GetPendingApprovals(session)
            .Where(request => request.ToolCall is FunctionCallContent call && call.Name == pressedActionTool)];
        if (approvedByPress.Count > 0)
        {
            agentLogger.LogInformation(
                "Agent '{AgentIntent}' runs {Tool}: the user approved it by pressing its button", AgentIntent, pressedActionTool);

            // The user's approval is recorded as the answer to the pending calls, so the model resumes with them.
            aiChatHistoryProvider.AppendMessage(session, new ChatMessage(ChatRole.User,
                [.. approvedByPress.Select(AIContent (request) => request.CreateResponse(true))]) { CreatedAt = DateTimeOffset.UtcNow });
            await RunModelAsync();
        }

        // A turn that asks the user to approve a tool ends here, whatever the model wrote: running it
        // again would only meet the same request still unanswered.
        bool awaitsApproval = GetPendingApprovals(session).Count > 0;

        // The turn's text is the one that Reply recorded; a turn awaiting approval may hold none yet.
        string turnText = GetClosingDeclarationFromContext(session)?.Text.Trim() ?? string.Empty;

        // A turn the user would receive as an empty bubble is run once more: the session already
        // holds whatever the first pass did, so the model picks up from there and gives its answer.
        if (turnText.Length == 0 && !awaitsApproval)
        {
            agentLogger.LogWarning("Agent '{AgentIntent}' ended its turn with no answer: running it once more", AgentIntent);
            await RunModelAsync();
            turnText = GetClosingDeclarationFromContext(session)?.Text.Trim() ?? string.Empty;
        }

        // Still nothing to say: the turn has failed and the user is told so, rather than left facing silence.
        if (turnText.Length == 0 && !awaitsApproval)
            throw new InvalidOperationException($"Agent '{AgentIntent}' gave no answer in two passes");

        // The text of the turn and whether it awaits approval are handed back for closing.
        return new Records.TurnOutcome(turnText, awaitsApproval);
    }

    /// <summary>Decides the text, buttons and card that the user receives, from the answer that the Reply tool recorded.</summary>
    private Records.TurnClosure ConcludeTurn(AgentSession session, Records.TurnOutcome turnOutcome, Records.Prompt morganaPrompt, Records.FrameworkReplies frameworkReplies, QuickReply? pressedFrameworkReply, bool isLanding)
    {
        // An approval asked with no word of the model's own is asked in Morgana's words.
        string llmResponseText = turnOutcome.Text;
        if (llmResponseText.Length == 0)
            llmResponseText = morganaPrompt.GetMessage(Constants.Messages.Approval);

        // How the turn closes, recorded by Reply whether the model called it or the framework closed
        // the turn on its last words. A turn that still declares nothing is answered and ends.
        Records.ClosingDeclaration closingDeclaration = GetClosingDeclarationFromContext(session)
                                      ?? new Records.ClosingDeclaration(string.Empty, Records.AwaitedFromUser.Nothing, false, [], null);

        // A Reply held beside an approval request never moves the workflow nor offers its buttons: in that turn only its text counts.
        if (turnOutcome.AwaitsApproval)
            closingDeclaration = new Records.ClosingDeclaration(turnOutcome.Text, Records.AwaitedFromUser.Nothing, false, [], null);

        // A press of the button to leave ends the stay whatever the model answered, so the user is back with Morgana.
        // A press of the button to stay hands the user the floor: the turn awaits free text and offers nothing to press.
        closingDeclaration = pressedFrameworkReply switch
        {
            { Termination: true } => closingDeclaration with { UserIsLeaving = true },
            { } => closingDeclaration with { Awaits = Records.AwaitedFromUser.TypedAnswer, Actions = [] },
            null => closingDeclaration
        };

        // The closure belongs to this turn alone: left in the session it would close the next turn too.
        aiContextProvider.DropVariable(session, Constants.ContextKeys.ClosingDeclaration);

        // The landing ends with the turn, before the session is saved: a restart must not find it set.
        aiContextProvider.DropVariable(session, Constants.ContextKeys.Landing);

        // The run's instructions end with the turn too, before the session is saved: a restart must not find them set.
        aiContextProvider.DropVariable(session, Constants.ContextKeys.RunInstructions);

        // The mark of the pressed call made ends with the turn too, before the session is saved: a restart must not find it set.
        aiContextProvider.DropVariable(session, Constants.ContextKeys.PressedCallMade);

        // The consultation budget is spent per turn, before the session is saved: a restart must not start the next turn with it already spent.
        aiContextProvider.DropVariable(session, Constants.ContextKeys.ConsultationRounds);

        // The workflow position is touched only by a leaving user or by a turn that awaits no approval:
        // a turn awaiting approval keeps the approval buttons alone.
        switch (closingDeclaration.UserIsLeaving)
        {
            // A user who leaves abandons the workflow where it stands, undoing nothing. Silence, a disconnection
            // or a restart leave it in place, so the next message resumes at the pending step.
            case true:
                aiContextProvider.DropWorkflowPosition(session);
                break;
            // A workflow that still stands at a choice step offers its tools as the buttons, whatever the model
            // closed with: a model that never complied, the framework's own closure and a missing Reply all end here.
            case false when !turnOutcome.AwaitsApproval:
            {
                // A workflow standing at a choice step offers the tools of that step as buttons.
                if (aiContextProvider.GetWorkflowPosition(session)?.Resolve(aiContextProvider.Workflows) is { Step.Tools.Count: > 1 } choicePosition)
                    closingDeclaration = closingDeclaration.WithStepActions(choicePosition.Step.Tools);
                break;
            }
        }

        // Outside a workflow the agent's buttons are its menu on every turn, whatever the model closed with: the buttons are
        // written by code and the model only gives them a voice. A turn asking the user to type keeps no button, except
        // the landing, which is the agent presenting itself. A running workflow offers what its step allows instead.
        bool closesOnMenu = isLanding
                            || (closingDeclaration.Awaits != Records.AwaitedFromUser.TypedAnswer && aiContextProvider.GetWorkflowPosition(session) is null);
        if (closesOnMenu && !closingDeclaration.UserIsLeaving && !turnOutcome.AwaitsApproval)
            closingDeclaration = closingDeclaration.WithMenu(aiContextProvider.Menu);

        // Every turn gets the buttons its closure calls for, the way out included, but two.
        (List<QuickReply>? quickReplies, bool isCompleted) = (turnOutcome.AwaitsApproval, pressedFrameworkReply) switch
        {
            // A turn waiting for an approval offers the two answers to it, then the way out, whose press declines it as anything else does.
            (true, _) => ([.. frameworkReplies.Approval ?? [], .. frameworkReplies.Escape], false),
            // The turn answering a press of the button to stay offers no button: offering the way out again would only loop it.
            (false, { Termination: not true }) => (null, false),
            // Any other turn is delivered as its closure says.
            _ => closingDeclaration.ToDelivery(frameworkReplies)
        };
        // An approval question carries no card: the user is asked one thing.
        RichCard? richCard = turnOutcome.AwaitsApproval ? null : closingDeclaration.Card;

        // The actions delivered as buttons are what the next message is read against for a press; an approval turn delivers none.
        aiContextProvider.SetOfferedActions(session, turnOutcome.AwaitsApproval ? [] : closingDeclaration.DeliveredActions);

        // A pressed call outlives its turn only while the turn asks the user to type what it lacks. Any other closure lets it go,
        // so a later message is never bound to a call that nobody is waiting to complete.
        if (aiContextProvider.GetPressedCall(session) is { } pressedCall
            && !(pressedCall.AwaitingAnswer && !turnOutcome.AwaitsApproval
                 && closingDeclaration is { Awaits: Records.AwaitedFromUser.TypedAnswer, UserIsLeaving: false }))
            aiContextProvider.DropPressedCall(session);

        // The line the harness and the operator read to see how the turn closed.
        agentLogger.LogInformation(
            "Agent response analysis: Awaits={Awaits}, UserIsLeaving={UserIsLeaving}, QuickReplies={QuickReplies}, HasRichCard={HasRichCard}, IsCompleted={IsCompleted}",
            closingDeclaration.Awaits, closingDeclaration.UserIsLeaving, quickReplies?.Count ?? 0, richCard is not null, isCompleted);

        // The closure carries the text, buttons, card and completion on to the delivery.
        return new Records.TurnClosure(llmResponseText, quickReplies, richCard, isCompleted, closingDeclaration.UserIsLeaving);
    }

    /// <summary>Writes the delivered turn into the history and the record then closes the turn's span.</summary>
    private async Task<DateTime?> RecordTurnAsync(AgentSession session, Records.TurnClosure closure, int historyBaseline, Activity? agentSpan)
    {
        // The span carries the turn's outcome and nothing of the user's content beyond a short preview.
        string responsePreview = Preview(closure.Text);
        agentSpan?.SetTag(Telemetry.AgentIsCompleted, closure.IsCompleted);
        agentSpan?.SetTag(Telemetry.AgentHasQuickReplies, closure.QuickReplies is not null);
        agentSpan?.SetTag(Telemetry.AgentToolsInvoked, GetToolsInvoked(session, historyBaseline));
        agentSpan?.SetTag(Telemetry.AgentResponsePreview, responsePreview);
        agentSpan?.Dispose();

        // The turn's messages after the baseline: the candidates for the one the user reads.
        List<ChatMessage> turnMessages = [.. aiChatHistoryProvider.GetMessages(session).Skip(historyBaseline)];

        // The message holding the Reply call is the one the user reads: it receives the delivery marks.
        // Failing that the framework closed the turn on the model's last words, which are the message to mark.
        // Failing that the Reply was dropped beside an approval request: the marks carry the delivered text, so the
        // message that asked for approval receives them and nothing is inserted between a call and its result.
        ChatMessage? finalAssistantMessage = turnMessages.LastOrDefault(message => message.Role == ChatRole.Assistant
                                                  && message.Contents.OfType<FunctionCallContent>().Any(call => call.Name == Constants.Tools.Reply))
                                              ?? turnMessages.LastOrDefault(message => message.Role == ChatRole.Assistant
                                                  && message.Contents.OfType<TextContent>().Any(text => !string.IsNullOrWhiteSpace(text.Text)))
                                              ?? turnMessages.LastOrDefault(message => message.Role == ChatRole.Assistant);

        // A turn has one user-facing message. Approving a call makes the tool loop re-emit a copy of the message
        // that asked for it, its delivery marks included: left there, the transcript would show that question twice.
        foreach (ChatMessage turnMessage in turnMessages.Where(message => message != finalAssistantMessage))
            DropDeliveryMarks(turnMessage);

        // Text that Morgana supplied in place of the model's has no message of its own yet: it gets one, so the
        // transcript read back on resume shows it with its buttons.
        if (finalAssistantMessage is null && closure.Text.Length > 0)
        {
            // The text Morgana supplied gets a message of its own, dated now.
            finalAssistantMessage = new ChatMessage(ChatRole.Assistant, closure.Text) { CreatedAt = DateTimeOffset.UtcNow };
            // The new message joins the history, so the transcript holds it.
            aiChatHistoryProvider.AppendMessage(session, finalAssistantMessage);
        }
        // The marks make the message appear in the transcript with the text, buttons and card that were delivered.
        if (finalAssistantMessage is not null)
        {
            // The delivery marks go on the message, so the history can later tell what the user was shown.
            finalAssistantMessage.AdditionalProperties ??= new AdditionalPropertiesDictionary();
            finalAssistantMessage.AdditionalProperties[Constants.MessageProperties.UserFacing] = true;
            finalAssistantMessage.AdditionalProperties[Constants.MessageProperties.TurnText] = closure.Text;

            // What the user was offered beside the text, kept as delivered so a transcript shows the
            // same buttons and card whoever composed them.
            if (closure.QuickReplies is not null)
                finalAssistantMessage.AdditionalProperties[Constants.MessageProperties.TurnQuickReplies] =
                    JsonSerializer.Serialize(closure.QuickReplies, Records.DefaultJsonSerializerOptions);
            if (closure.Card is not null)
                finalAssistantMessage.AdditionalProperties[Constants.MessageProperties.TurnRichCard] =
                    JsonSerializer.Serialize(closure.Card, Records.DefaultJsonSerializerOptions);

            // The user left on this turn: whatever they bring next opens a new episode, read on its own.
            if (closure.UserIsLeaving)
                finalAssistantMessage.AdditionalProperties[Constants.MessageProperties.EpisodeEnd] = true;
        }

        // Written last, so what lands in the database is the history already carrying the
        // user-facing marks the history endpoint reads back. Before the sender is answered: a turn
        // the caller was told about but whose state never persisted would come back missing on
        // resume. The completion flag travels with it as the row's active state, which is what a
        // resumed conversation restores.
        await persistenceService.SaveAgentConversationAsync(AgentIdentifier, aiAgent, session, closure.IsCompleted);
        agentLogger.LogInformation("Saved conversation state for {AgentIdentifier}", AgentIdentifier);

        // The message's date is handed back, so the caller can stamp the reply with it.
        return finalAssistantMessage?.CreatedAt?.UtcDateTime;
    }

    /// <summary>
    /// Answers a colleague of the same conversation, running a full LLM turn whose reader is another
    /// agent instead of the user.
    /// </summary>
    protected virtual async Task ServeConsultationAsync(Records.PeerConsultation consultation)
    {
        // The sender is the hosted agent waiting for the answer: captured before the turn yields.
        IActorRef senderRef = Sender;

        // Parented on the caller's turn context, which travelled with the question: the answer shows in
        // the trace nested under the work of the agent that asked, not as a turn of its own.
        Activity? consultationSpan = Telemetry.Source.StartActivity(
            Telemetry.ConsultationActivity,
            ActivityKind.Internal,
            consultation.TurnContext);

        // The tags let a trace tell who asked whom and what.
        consultationSpan?.SetTag(Telemetry.ConversationId, conversationId);
        consultationSpan?.SetTag(Telemetry.ConsultationCaller, consultation.CallerIntent);
        consultationSpan?.SetTag(Telemetry.ConsultationTarget, AgentIntent);
        consultationSpan?.SetTag(Telemetry.ConsultationQuestion, consultation.Question);

        try
        {
            // Born for this answer and nothing else: no history is loaded, so what the agent knows
            // here is its tools and what follows, never a conversation it had with the user or with
            // another colleague.
            consultationSession = await aiAgent.CreateSessionAsync();

            // The colleague may know things this agent has never been told: shared variables are
            // hydrated exactly as on a user turn, which is what makes a consultation answerable
            // without asking the asker for what the conversation already established. This is the
            // only channel reaching the ephemeral session and it carries values, never history.
            Dictionary<string, object> sharedFromRegistry = await persistenceService.LoadSharedVariablesAsync(conversationId);
            if (sharedFromRegistry.Count > 0)
                aiContextProvider.MergeSharedContext(consultationSession, sharedFromRegistry);

            // Marks this turn as served for a colleague. Read by MorganaPeerGuardAgent to refuse a
            // chained consultation and read for its presence alone — so the mark is a flag and not
            // the caller's name, which a caller that is not an agent of this installation never sent.
            // Who asked is on the span and in the line below, where something actually reads it.
            await aiContextProvider.SetVariableAsync(consultationSession, Constants.ContextKeys.ServingConsultation, true);

            // The rules for answering a colleague are this run's instructions: the ephemeral session dies
            // with the answer, so they never outlive it.
            if (consultation.Instructions is not null)
                await aiContextProvider.SetVariableAsync(consultationSession, Constants.ContextKeys.RunInstructions, consultation.Instructions);

            agentLogger.LogInformation(
                "Agent '{AgentIntent}' is answering a consultation from '{CallerIntent}'", AgentIntent, consultation.CallerIntent);

            // The colleague's question is the turn's message, fenced as somebody else's text, because a
            // user message is the only role a turn can start from; who really asked and what the text may
            // not claim travel in the run's instructions. Never streamed: the reader is an agent waiting
            // for one whole answer, not a channel.
            await aiAgent.RunAsync(
                new ChatMessage(ChatRole.User, consultation.Question) { CreatedAt = DateTimeOffset.UtcNow },
                consultationSession);

            // The colleague's presentation decisions are handed over as data rather than drained:
            // the asking agent reads the options it was offered and may come back having chosen one.
            Records.ClosingDeclaration? closingDeclaration = GetClosingDeclarationFromContext(consultationSession);
            bool awaitsReply = closingDeclaration?.AwaitsUser == true;

            // The colleague's answer is the text of its Reply: whatever else it wrote never leaves the session.
            string consultationAnswer = closingDeclaration?.Text.Trim() ?? string.Empty;

            // The colleague's own actions and nothing of the framework's: staying or leaving is the
            // asking agent's user's choice, never the colleague's.
            List<QuickReply>? quickReplies = closingDeclaration?.ToDelivery(new Records.FrameworkReplies([], [])).QuickReplies;
            RichCard? richCard = closingDeclaration?.Card;

            // A baseline of 0 where a user turn passes its own: this session was created for the
            // exchange and holds nothing else, so every tool call in it belongs to this answer and
            // there is no earlier history to skip past.
            consultationSpan?.SetTag(Telemetry.ConsultationAwaitingReply, awaitsReply || quickReplies?.Count > 0);
            consultationSpan?.SetTag(Telemetry.AgentToolsInvoked, GetToolsInvoked(consultationSession, 0));
            consultationSpan?.SetTag(Telemetry.ConsultationAnswer, consultationAnswer);
            consultationSpan?.Dispose();

            // Nothing is persisted and that is the design rather than an omission: what this turn
            // wrote dies with the session it wrote it on and the agent's own row — if it has one —
            // is left exactly as the user's last turn left it.

            // The answer, its buttons and its card travel back as one response for the asking agent to read.
            senderRef.Tell(new Records.PeerConsultationResponse(
                consultationAnswer,
                awaitsReply || quickReplies?.Count > 0,
                quickReplies,
                richCard));
        }
        catch (Exception ex)
        {
            agentLogger.LogError(
                ex, "Agent '{AgentIntent}' failed to answer a consultation from '{CallerIntent}'", AgentIntent, consultation.CallerIntent);
            consultationSpan?.SetStatus(ActivityStatusCode.Error, ex.Message);
            consultationSpan?.AddException(ex);
            consultationSpan?.Dispose();

            // What the asking agent's model reads in place of an answer, worded in morgana.json.
            Records.Prompt morganaPrompt = await promptResolverService.ResolveAsync(Constants.Morgana);
            senderRef.Tell(new Records.PeerConsultationResponse(
                Records.Injection.Resolve(
                    morganaPrompt.GetAdditionalPropertyOrDefault<List<Records.Injection>>(Constants.PromptProperties.ToolInjections, []),
                    Constants.ToolInjections.ColleagueCouldNotAnswer,
                    new Dictionary<string, string> { [Constants.Placeholders.AgentIntent] = AgentIntent }),
                false));
        }
        finally
        {
            // The failure path included: with the field cleared nothing holds a reference to that
            // session any more and CurrentSession points back at the agent's own. The keys this
            // turn wrote need no dropping — they were written on a session now unreachable.
            consultationSession = null;
        }
    }

    /// <summary>Answers the waiting sender with Morgana's generic error after a turn threw.</summary>
    private async Task HandleAgentFailureAsync(Records.FailureContext failure)
    {
        agentLogger.LogError(failure.Failure.Cause, "Agent execution failed in {Name}", GetType().Name);

        // The error is worded in morgana.json, in Morgana's own voice.
        Records.Prompt morganaPrompt = await promptResolverService.ResolveAsync(Constants.Morgana);

        // The turn is closed as completed: the user is not left in a conversation that is waiting on a dead turn.
        failure.OriginalSender.Tell(new Records.AgentResponse(morganaPrompt.GetMessage(Constants.Messages.GenericError), true, null));
    }

    /// <summary>Removes from a message every mark that names it the user-facing message of its turn.</summary>
    private static void DropDeliveryMarks(ChatMessage message)
    {
        // A message with no properties carries no mark.
        if (message.AdditionalProperties is not { } messageProperties)
            return;

        // Every mark that the delivery writes goes, so the transcript reads the message as the turn's working.
        foreach (string deliveryMark in (string[])[Constants.MessageProperties.UserFacing, Constants.MessageProperties.TurnText,
                     Constants.MessageProperties.TurnQuickReplies, Constants.MessageProperties.TurnRichCard, Constants.MessageProperties.EpisodeEnd])
            messageProperties.Remove(deliveryMark);
    }

    /// <summary>
    /// Trims a text down to what a span attribute may carry: enough for a human reading a trace to
    /// recognise what was said, never the whole of it — every attribute reaches every exporter.
    /// </summary>
    /// <param name="text">Text to preview; null is treated as empty.</param>
    /// <returns>The first 150 characters.</returns>
    private static string Preview(string? text)
        => text is null ? string.Empty : text.Length > 150 ? text[..150] : text;

    /// <summary>
    /// Collects, in call order, the names of the tools invoked during the current turn, for the <c>agent.tools_invoked</c> span attribute.
    /// </summary>
    /// <param name="session">Active agent session.</param>
    /// <param name="historyBaseline">Number of history messages present before the turn ran; everything past it belongs to this turn.</param>
    /// <returns>Comma-separated tool names in call order, repetitions kept since a repeated call is itself a signal; empty when the turn called no tool.</returns>
    protected string GetToolsInvoked(AgentSession session, int historyBaseline)
    {
        // Everything appended since the baseline is this turn's work.
        List<AIContent> turnContents = [.. aiChatHistoryProvider.GetMessages(session)
            .Skip(historyBaseline)
            .SelectMany(m => m.Contents)];

        // A call the user declined stays in the history beside its rejection but never ran: counted, the span
        // would report as done the very act that the user refused.
        HashSet<string> declined = [.. turnContents.OfType<ToolApprovalResponseContent>()
            .Where(response => !response.Approved)
            .Select(response => response.ToolCall)
            .OfType<FunctionCallContent>()
            .Select(call => call.CallId)];

        // The calls that ran, in the order the model made them.
        return string.Join(", ", turnContents.OfType<FunctionCallContent>()
            .Where(call => !declined.Contains(call.CallId))
            .Select(call => call.Name));
    }

    /// <summary>
    /// The call that the user's message commits the turn to: the press of an action button that the agent's last turn
    /// delivered or, for any other message, the call of an earlier press still waiting for the user's typed answer.
    /// Null when the message is neither, typed text with no press waiting included.
    /// </summary>
    /// <param name="session">Active agent session, read before the user's message joins it.</param>
    /// <param name="userText">What the user sent: a pressed button sends its own value.</param>
    protected Records.PressedCall? PressedCallOf(AgentSession session, string? userText)
    {
        // A press sends the button's own value, so the text that matches the value of a delivered action is that action's press.
        Records.OfferedAction? pressedAction = string.IsNullOrWhiteSpace(userText)
            ? null
            : aiContextProvider.GetOfferedActions(session)
                .FirstOrDefault(action => string.Equals(action.Value, userText.Trim(), StringComparison.OrdinalIgnoreCase));

        // A press commits the turn to its call, with the arguments that the button carries.
        if (pressedAction is not null)
            return Records.PressedCall.Of(pressedAction);

        // Any other message is the answer to a press waiting for a typed value, where one waits; a press never made does not carry over.
        return aiContextProvider.GetPressedCall(session) is { AwaitingAnswer: true } awaitingCall ? awaitingCall : null;
    }

    /// <summary>
    /// The approval requests in the agent's history that no answer of the user's has met yet.
    /// </summary>
    /// <param name="session">Active agent session.</param>
    protected List<ToolApprovalRequestContent> GetPendingApprovals(AgentSession session)
    {
        // A request is pending until a response with its id exists anywhere in the stored history.
        List<AIContent> contents = [.. aiChatHistoryProvider.GetMessages(session).SelectMany(message => message.Contents)];
        HashSet<string> answered = [.. contents.OfType<ToolApprovalResponseContent>().Select(response => response.RequestId)];

        // The requests that no answer has met are returned, in the order they were asked.
        return [.. contents.OfType<ToolApprovalRequestContent>().Where(request => !answered.Contains(request.RequestId))];
    }

    /// <summary>
    /// Reads the closure that the Reply tool recorded on the current turn.
    /// </summary>
    /// <param name="session">Active agent session.</param>
    /// <returns>The turn's closure; <c>null</c> when none was recorded.</returns>
    protected Records.ClosingDeclaration? GetClosingDeclarationFromContext(AgentSession session)
    {
        try
        {
            // The closure is the record that Reply serialized; with none recorded the turn is answered as if Reply had said nothing.
            return aiContextProvider.GetClosingDeclaration(session);
        }
        catch (JsonException ex)
        {
            // An unreadable closure is treated as none: the turn is answered and ends instead of failing after the model has spoken.
            agentLogger.LogError(ex, "Failed to read the turn's closure from context");
            return null;
        }
    }
}