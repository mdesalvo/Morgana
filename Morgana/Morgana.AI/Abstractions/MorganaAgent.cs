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
using Morgana.AI.Telemetry;
using Morgana.Contracts;
using Status = Akka.Actor.Status;

namespace Morgana.AI.Abstractions;

/// <summary>
/// Base class for domain-specific conversational agents in the Morgana framework.
/// Extends <see cref="MorganaActor"/> with AI agent capabilities, session management,
/// conversation context and inter-agent communication.
/// </summary>
/// <remarks>
/// Providers (MorganaAIContextProvider, MorganaChatHistoryProvider) are singletons on AIAgent,
/// shared across sessions. Per-session state in AgentSession (serialized by framework).
/// CurrentSession set per-turn, single-threaded. OTel: morgana.agent span as child of TurnContext,
/// TTFT event, tags: agent.ttft_ms, agent.response_preview, agent.is_completed.
/// </remarks>
public class MorganaAgent : MorganaActor
{
    /// <summary>
    /// Underlying Microsoft.Agents.AI agent driving LLM interactions for this actor.
    /// Created once in the subclass constructor and reused across turns.
    /// </summary>
    protected AIAgent aiAgent;

    /// <summary>
    /// Active <see cref="AgentSession"/> for the current conversation.
    /// Loaded from persistence on the first turn, then mutated in place across turns.
    /// Null before the first <see cref="ExecuteAgentAsync"/> call.
    /// </summary>
    protected AgentSession? aiAgentSession;

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
    /// The throwaway session a consultation is answered on, non-null only for that turn.
    /// </summary>
    private AgentSession? consultationSession;

    /// <summary>
    /// The active <see cref="AgentSession"/> for the current turn.
    /// Exposed so that tool closures can pass it to provider calls (GetVariable, SetVariable, etc.).
    /// Always non-null during a live agent invocation.
    /// </summary>
    /// <remarks>
    /// While a colleague is being answered this is the consultation's own session, not the agent's:
    /// that turn's tools and the guard refusing a chained consultation, must all read the session
    /// the turn is actually running on.
    /// </remarks>
    public AgentSession? CurrentSession
        => consultationSession ?? aiAgentSession;

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
        ILLMService llmService,
        IPromptResolverService promptResolverService,
        IConversationPersistenceService persistenceService,
        ILogger agentLogger,
        IConfiguration configuration) : base(conversationId, llmService, promptResolverService, configuration)
    {
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

        return aiAgentSession;
    }

    /// <summary>
    /// Callback invoked by <see cref="MorganaAIContextProvider"/> when a shared context variable
    /// is set. Persists the variable to the conversation-scoped <c>shared_context</c> registry so
    /// that any agent in the conversation — alive, dormant, dead-and-rehydrated, or never yet
    /// activated — can pick it up at the start of its next turn via
    /// <see cref="IConversationPersistenceService.LoadSharedVariablesAsync"/>.
    /// </summary>
    /// <remarks>
    /// <para>The persistence-based model writes once and lets each interested agent read on
    /// demand at the start of its next turn. Agents that never become active in a conversation
    /// pay zero cost; a write reaches an agent only if and when that agent actually runs.</para>
    /// </remarks>
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
            agentLogger.LogInformation(
                "Agent {AgentIntent} is serving a consultation: shared context variable '{Key}' stays local to the exchange", AgentIntent, key);
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
        IActorRef? senderRef = Sender;

        // Open morgana.agent span as child of the turn span propagated from the supervisor.
        // The span stays open for the full duration of LLM streaming so TTFT can be recorded.
        Activity? agentSpan = MorganaTelemetry.Source.StartActivity(
            MorganaTelemetry.AgentActivity,
            ActivityKind.Internal,
            req.TurnContext);

        agentSpan?.SetTag(MorganaTelemetry.ConversationId, conversationId);
        agentSpan?.SetTag(MorganaTelemetry.AgentName, GetType().Name);
        agentSpan?.SetTag(MorganaTelemetry.AgentIntent, AgentIntent);

        try
        {
            // Read from the database once per actor lifetime: on later turns the field already holds
            // the live session and re-reading would discard everything this actor has appended since.
            // The agent hands itself over because deserializing a session is its own responsibility.
            aiAgentSession ??= await persistenceService.LoadAgentConversationAsync(AgentIdentifier, this);
            if (aiAgentSession != null)
            {
                agentLogger.LogInformation("Loaded existing conversation session for {AgentIdentifier}", AgentIdentifier);

                agentSpan?.AddEvent(new ActivityEvent(MorganaTelemetry.ResumeAgentConversation));
            }
            else
            {
                // No row under this identifier: first time this agent is activated in the conversation.
                // It starts with an empty history — the shared registry below is all it inherits.
                aiAgentSession = await aiAgent.CreateSessionAsync();

                agentLogger.LogInformation("Created new conversation session for {AgentIdentifier}", AgentIdentifier);

                agentSpan?.AddEvent(new ActivityEvent(MorganaTelemetry.CreateAgentConversation));
            }
            agentSpan?.SetTag(MorganaTelemetry.AgentIdentifier, AgentIdentifier);

            // Hydrate the agent's local context from the conversation-scoped shared_context
            // registry. Shared variables produced by any other agent of this conversation —
            // whether currently alive, dormant, dead-and-rehydrated, or never yet activated —
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

            // Stamped server-side because the history of a conversation is reassembled by merging every
            // agent's own session chronologically: a message without a timestamp cannot be placed.
            ChatMessage userMessage = new ChatMessage(ChatRole.User, req.Content!) { CreatedAt = DateTimeOffset.UtcNow };

            // History length before this turn runs: everything appended past this point belongs to
            // the turn, which is what agent.tools_invoked must report — the span is per-turn, so
            // reporting the whole session would attribute every past tool call to this one.
            int historyBaseline = aiChatHistoryProvider.GetMessages(aiAgentSession).Count;

            // Streaming is gated on two independent signals:
            //   1. Global config flag (Morgana:AdaptiveMessaging:EnableStreamingResponse)
            //   2. Channel capability — we don't even attach to the LLM streaming endpoint
            //      when the outbound channel can't deliver chunks to the user. When Capabilities
            //      is null (legacy/test paths) we assume the channel supports streaming.
            bool streamingConfigEnabled = configuration.GetValue("Morgana:AdaptiveMessaging:EnableStreamingResponse", true);
            bool channelSupportsStreaming = req.Capabilities?.SupportsStreaming ?? true;
            bool useStreaming = streamingConfigEnabled && channelSupportsStreaming;

            if (!channelSupportsStreaming)
                agentLogger.LogInformation("Agent '{AgentIntent}' bypassing LLM streaming: channel does not advertise SupportsStreaming", AgentIntent);

            StringBuilder fullResponse = new StringBuilder();
            if (useStreaming)
            {
                Stopwatch firstChunkStopwatch = Stopwatch.StartNew();
                bool firstChunkEmitted = false;
                string? lastTextMessageId = null;

                // The whole turn runs here — tool calls included, which surface as chunks carrying no text.
                // The session is written as the stream advances, so it is already complete when it ends.
                await foreach (AgentResponseUpdate chunk in aiAgent.RunStreamingAsync(userMessage, aiAgentSession))
                {
                    if (!string.IsNullOrEmpty(chunk.Text))
                    {
                        // Two text chunks with different MessageIds come from different messages and that
                        // is where the separator belongs. Within one message the chunks are tokens and must
                        // stay welded, so only text-carrying chunks update the id. A provider that never
                        // sets MessageId reports no boundary and nothing is inserted.
                        if (lastTextMessageId is not null
                             && !string.Equals(chunk.MessageId, lastTextMessageId, StringComparison.Ordinal)
                             && NeedsMessageSeparator(fullResponse, chunk.Text))
                        {
                            fullResponse.Append(Constants.Markers.MessageSeparator);

                            // Streamed too, so the live text matches the final one the client is about to
                            // overwrite it with, instead of showing the weld for the rest of the turn.
                            senderRef.Tell(new Records.AgentStreamChunk(Constants.Markers.MessageSeparator));
                        }

                        lastTextMessageId = chunk.MessageId;

                        fullResponse.Append(chunk.Text);
                        senderRef.Tell(new Records.AgentStreamChunk(chunk.Text));

                        // Record time-to-first-token on the very first chunk
                        if (!firstChunkEmitted)
                        {
                            firstChunkEmitted = true;
                            long ttft = firstChunkStopwatch.ElapsedMilliseconds;
                            firstChunkStopwatch.Stop();
                            agentSpan?.AddEvent(new ActivityEvent(MorganaTelemetry.EventFirstChunk));
                            agentSpan?.SetTag(MorganaTelemetry.AgentTtftMs, ttft);
                            MorganaTelemetry.AgentTtftHistogram.Record(ttft);
                        }
                    }
                }
            }
            else
            {
                Stopwatch responseStopwatch = Stopwatch.StartNew();
                // Same turn as the streaming branch, awaited whole: nothing reaches the channel until the
                // model and every tool it decided to call, are done.
                AgentResponse response = await aiAgent.RunAsync(userMessage, aiAgentSession);
                responseStopwatch.Stop();

                // Assembled message by message rather than through AgentResponse.Text, which is
                // documented to concatenate every message's text and so produces exactly the weld
                // Markers.MessageSeparator exists to prevent. Here every element is a whole message, so the
                // boundary needs no detecting — unlike the streaming path above.
                foreach (ChatMessage responseMessage in response.Messages)
                {
                    if (string.IsNullOrEmpty(responseMessage.Text))
                        continue;

                    if (NeedsMessageSeparator(fullResponse, responseMessage.Text))
                        fullResponse.Append(Constants.Markers.MessageSeparator);

                    fullResponse.Append(responseMessage.Text);
                }

                long ttft = responseStopwatch.ElapsedMilliseconds;
                agentSpan?.AddEvent(new ActivityEvent(MorganaTelemetry.EventFirstChunk));
                agentSpan?.SetTag(MorganaTelemetry.AgentTtftMs, ttft);
                MorganaTelemetry.AgentTtftHistogram.Record(ttft);
            }

            string llmResponseText = fullResponse.ToString().Trim();

            #region LLM tools
            // TurnContinuation
            bool wantsContinuation = GetTurnContinuationFromContext(aiAgentSession);
            aiContextProvider.DropVariable(aiAgentSession, Constants.ContextKeys.TurnContinuation);

            // QuickReplies
            List<QuickReply>? quickReplies = GetQuickRepliesFromContext(aiAgentSession);
            bool hasQuickReplies = quickReplies?.Count > 0;
            if (hasQuickReplies)
            {
                aiContextProvider.DropVariable(aiAgentSession, Constants.ContextKeys.QuickReplies);
                agentLogger.LogInformation("Dropped {Count} quick replies from context (ephemeral data)", quickReplies!.Count);
            }

            // RichCard
            RichCard? richCard = GetRichCardFromContext(aiAgentSession);
            bool hasRichCard = richCard != null;
            if (hasRichCard)
            {
                aiContextProvider.DropVariable(aiAgentSession, Constants.ContextKeys.RichCard);
                agentLogger.LogInformation("Dropped rich card '{Title}' from context (ephemeral data)", richCard!.Title);
            }
            #endregion

            // Determine turn continuation strategy, depending on LLM output
            bool isCompleted = !wantsContinuation && !hasQuickReplies && !hasRichCard;

            agentLogger.LogInformation(
                "Agent response analysis:" +
                $"WantsContinuation={wantsContinuation}," +
                $"HasQuickReplies={hasQuickReplies}," +
                $"HasRichCard={hasRichCard}," +
                $"IsCompleted={isCompleted}");

            // Finalize agent span with outcome attributes
            string responsePreview = Preview(llmResponseText);
            agentSpan?.SetTag(MorganaTelemetry.AgentIsCompleted, isCompleted);
            agentSpan?.SetTag(MorganaTelemetry.AgentHasQuickReplies, hasQuickReplies);
            agentSpan?.SetTag(MorganaTelemetry.AgentToolsInvoked, GetToolsInvoked(aiAgentSession, historyBaseline));
            agentSpan?.SetTag(MorganaTelemetry.AgentResponsePreview, responsePreview);
            agentSpan?.Dispose();

            // The exchange with a colleague is spent once it has been read. Clearing it here keeps
            // it out of the caller's own session — see StripPeerConsultations — and is done after
            // the span has been tagged, so telemetry still records that the colleague was consulted.
            StripPeerConsultations(aiAgentSession, historyBaseline);

            // Tag this turn's user-facing assistant message — the LAST assistant message that
            // actually carries text content.
            ChatMessage? finalAssistantMessage = aiChatHistoryProvider
                .GetMessages(aiAgentSession)
                .LastOrDefault(m => m.Role == ChatRole.Assistant
                                     && m.Contents.OfType<TextContent>().Any(t => !string.IsNullOrWhiteSpace(t.Text)));
            if (finalAssistantMessage is not null)
            {
                finalAssistantMessage.AdditionalProperties ??= new AdditionalPropertiesDictionary();
                finalAssistantMessage.AdditionalProperties[Constants.MessageProperties.UserFacing] = true;
                finalAssistantMessage.AdditionalProperties[Constants.MessageProperties.TurnText] = llmResponseText;
            }

            // Written last, so what lands in the database is the history already stripped of the
            // consultations and already carrying the user-facing marks the history endpoint reads
            // back. Before the sender is answered: a turn the caller was told about but whose state
            // never persisted would come back missing on resume. The completion flag travels with
            // it as the row's active state, which is what a resumed conversation restores.
            await persistenceService.SaveAgentConversationAsync(AgentIdentifier, aiAgent, aiAgentSession, isCompleted);
            agentLogger.LogInformation("Saved conversation state for {AgentIdentifier}", AgentIdentifier);

            senderRef.Tell(new Records.AgentResponse(llmResponseText, isCompleted, quickReplies, richCard));
        }
        catch (Exception ex) when (ex is System.ClientModel.ClientResultException { Status: 400 } cre
                                     && cre.Message.Contains("content_filter", StringComparison.OrdinalIgnoreCase))
        {
            agentLogger.LogWarning(ex, "Content filter rejection in {Name} for conversation {ConversationId}", GetType().Name, conversationId);
            agentSpan?.SetStatus(ActivityStatusCode.Error, "content_filter");
            agentSpan?.AddException(ex);
            agentSpan?.Dispose();

            senderRef.Tell(new Records.ContentFilterRejection());
        }
        catch (Exception ex)
        {
            agentLogger.LogError(ex, "Error in {Name}", GetType().Name);
            agentSpan?.SetStatus(ActivityStatusCode.Error, ex.Message);
            agentSpan?.AddException(ex);
            agentSpan?.Dispose();

            Self.Tell(new Records.FailureContext(new Status.Failure(ex), senderRef));
        }
        finally
        {
            // Safety net: ephemeral UI variables (rich card, quick replies, ...) must NEVER leak to the next turn.
            if (aiAgentSession is not null)
            {
                aiContextProvider.DropVariable(aiAgentSession, Constants.ContextKeys.RichCard);
                aiContextProvider.DropVariable(aiAgentSession, Constants.ContextKeys.QuickReplies);
                aiContextProvider.DropVariable(aiAgentSession, Constants.ContextKeys.TurnContinuation);
                aiContextProvider.DropVariable(aiAgentSession, Constants.ContextKeys.ConsultationRounds);
            }
        }
    }

    /// <summary>
    /// Answers a colleague of the same conversation, running a full LLM turn whose reader is another
    /// agent instead of the user.
    /// </summary>
    protected virtual async Task ServeConsultationAsync(Records.PeerConsultation consultation)
    {
        IActorRef senderRef = Sender;

        // Parented on the caller's turn context, which travelled with the question: the answer shows in
        // the trace nested under the work of the agent that asked, not as a turn of its own.
        Activity? consultationSpan = MorganaTelemetry.Source.StartActivity(
            MorganaTelemetry.ConsultationActivity,
            ActivityKind.Internal,
            consultation.TurnContext);

        consultationSpan?.SetTag(MorganaTelemetry.ConversationId, conversationId);
        consultationSpan?.SetTag(MorganaTelemetry.ConsultationCaller, consultation.CallerIntent);
        consultationSpan?.SetTag(MorganaTelemetry.ConsultationTarget, AgentIntent);
        consultationSpan?.SetTag(MorganaTelemetry.ConsultationQuestion, consultation.Question);

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

            agentLogger.LogInformation(
                "Agent '{AgentIntent}' is answering a consultation from '{CallerIntent}'", AgentIntent, consultation.CallerIntent);

            // The colleague's question enters as a user message because that is the only role a turn can
            // start from; who really asked is carried by the declaration spliced in front of it. Never
            // streamed: the reader is an agent waiting for one whole answer, not a channel.
            AgentResponse response = await aiAgent.RunAsync(
                new ChatMessage(ChatRole.User, consultation.Question) { CreatedAt = DateTimeOffset.UtcNow },
                consultationSession);

            // The colleague's presentation decisions are handed over as data rather than drained:
            // the asking agent reads the options it was offered and may come back having chosen one.
            bool awaitsReply = GetTurnContinuationFromContext(consultationSession);
            List<QuickReply>? quickReplies = GetQuickRepliesFromContext(consultationSession);
            RichCard? richCard = GetRichCardFromContext(consultationSession);

            // A baseline of 0 where a user turn passes its own: this session was created for the
            // exchange and holds nothing else, so every tool call in it belongs to this answer and
            // there is no earlier history to skip past.
            consultationSpan?.SetTag(MorganaTelemetry.ConsultationAwaitingReply, awaitsReply || quickReplies?.Count > 0);
            consultationSpan?.SetTag(MorganaTelemetry.AgentToolsInvoked, GetToolsInvoked(consultationSession, 0));
            consultationSpan?.SetTag(MorganaTelemetry.ConsultationAnswer, response.Text);
            consultationSpan?.Dispose();

            // Nothing is persisted and that is the design rather than an omission: what this turn
            // wrote dies with the session it wrote it on and the agent's own row — if it has one —
            // is left exactly as the user's last turn left it.

            senderRef.Tell(new Records.PeerConsultationResponse(
                response.Text.Trim(),
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

            senderRef.Tell(new Records.PeerConsultationResponse(
                $"Your colleague for '{AgentIntent}' could not answer. Proceed without them.", false));
        }
        finally
        {
            // The failure path included: with the field cleared nothing holds a reference to that
            // session any more and CurrentSession points back at the agent's own. The keys this
            // turn wrote need no dropping — they were written on a session now unreachable.
            consultationSession = null;
        }
    }

    private async Task HandleAgentFailureAsync(Records.FailureContext failure)
    {
        agentLogger.LogError(failure.Failure.Cause, "Agent execution failed in {Name}", GetType().Name);

        Records.Prompt morganaPrompt = await promptResolverService.ResolveAsync(Constants.Morgana);
        List<Records.ErrorAnswer> errorAnswers = morganaPrompt.GetAdditionalProperty<List<Records.ErrorAnswer>>("ErrorAnswers");
        Records.ErrorAnswer? genericError = errorAnswers.FirstOrDefault(e => string.Equals(e.Name, "GenericError", StringComparison.OrdinalIgnoreCase));

        failure.OriginalSender.Tell(new Records.AgentResponse(genericError?.Content ?? "An internal error occurred.", true, null));
    }

    /// <summary>
    /// Removes this turn's peer-consultation calls and their results, from the agent's own history.
    /// </summary>
    /// <param name="historyBaseline">Messages present before the turn ran; earlier turns cleared themselves.</param>
    protected void StripPeerConsultations(AgentSession session, int historyBaseline)
    {
        // The session's live list, not a copy: removing from it is what actually clears the history.
        List<ChatMessage> messages = aiChatHistoryProvider.GetMessages(session);

        // Only this turn's consultations: a call id from an earlier turn was already cleared by it and
        // matching on the id is what keeps a call and its result together across two separate messages.
        HashSet<string> consultationCallIds =
        [
            .. messages.Skip(historyBaseline)
                .SelectMany(m => m.Contents.OfType<FunctionCallContent>())
                .Where(call => call.Name.StartsWith(Constants.AgentToAgent.PeerFunctionNamePrefix, StringComparison.Ordinal))
                .Select(call => call.CallId)
        ];

        // Nothing consulted this turn: leave the history untouched rather than walk it.
        if (consultationCallIds.Count == 0)
            return;

        // Backwards from the end down to the baseline: removals shift everything after them and the
        // messages before the baseline belong to earlier turns that already cleared themselves.
        for (int index = messages.Count - 1; index >= historyBaseline; index--)
        {
            ChatMessage message = messages[index];

            // One message can carry a consultation alongside a native tool call or the turn's own text,
            // so it is emptied content by content instead of being dropped whole.
            for (int contentIndex = message.Contents.Count - 1; contentIndex >= 0; contentIndex--)
            {
                // Call and result live in different messages and are recognised by the same id, which is
                // what removes them as the pair the provider requires them to be.
                bool belongsToConsultation = message.Contents[contentIndex] switch
                {
                    FunctionCallContent call => consultationCallIds.Contains(call.CallId),
                    FunctionResultContent result => consultationCallIds.Contains(result.CallId),
                    _ => false
                };

                // Removed in place: what the provider will resend on the next turn is this very list.
                if (belongsToConsultation)
                    message.Contents.RemoveAt(contentIndex);
            }

            // A message whose only content was the consultation is now empty and an empty message is
            // rejected on the next turn, so it goes with it.
            if (message.Contents.Count == 0)
                messages.RemoveAt(index);
        }

        agentLogger.LogInformation(
            "Agent '{AgentIntent}' cleared {Count} peer consultation(s) from its own history", AgentIntent, consultationCallIds.Count);
    }

    /// <summary>
    /// Whether <see cref="Constants.Markers.MessageSeparator"/> has to be inserted before appending <paramref name="incoming"/>
    /// to the text accumulated so far.
    /// </summary>
    /// <param name="accumulated">Response text assembled up to this point.</param>
    /// <param name="incoming">Text of the message about to be appended; never empty.</param>
    /// <returns><c>true</c> when the two would otherwise weld together.</returns>
    private static bool NeedsMessageSeparator(StringBuilder accumulated, string incoming)
        => accumulated.Length > 0
           && !char.IsWhiteSpace(accumulated[^1])
           && !char.IsWhiteSpace(incoming[0]);

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
    /// <returns>Comma-separated tool names in call order — repetitions kept, since a repeated call is itself a signal — or an empty string when the turn called no tool.</returns>
    protected string GetToolsInvoked(AgentSession session, int historyBaseline)
        => string.Join(", ", aiChatHistoryProvider.GetMessages(session)
            .Skip(historyBaseline)
            .SelectMany(m => m.Contents.OfType<FunctionCallContent>())
            .Select(c => c.Name));

    /// <summary>
    /// Reads the <c>turn_continuation</c> context variable, set by the <c>SetTurnContinuation</c>
    /// base tool when the agent declares it is staying in service awaiting the user's next turn.
    /// </summary>
    /// <param name="session">Active agent session.</param>
    /// <returns><c>true</c> if the agent declared continuation on this turn; <c>false</c> if it
    /// declared completion or made no declaration at all.</returns>
    protected bool GetTurnContinuationFromContext(AgentSession session)
    {
        object? ctxTurnContinuation = aiContextProvider.GetVariable(session, Constants.ContextKeys.TurnContinuation);
        return ctxTurnContinuation switch
        {
            bool continuation => continuation,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.False } => false,
            JsonElement { ValueKind: JsonValueKind.String } element => bool.TryParse(element.GetString(), out bool parsed) && parsed,
            string text => bool.TryParse(text, out bool parsed) && parsed,
            _ => false
        };
    }

    /// <summary>
    /// Reads and deserializes the <c>quick_replies</c> context variable, if the agent set one
    /// on the current turn via the <c>SetQuickReplies</c> base tool. Drops the variable if the
    /// stored JSON is malformed.
    /// </summary>
    /// <param name="session">Active agent session.</param>
    /// <returns>The deserialized quick replies, or <c>null</c> if absent or invalid.</returns>
    protected List<QuickReply>? GetQuickRepliesFromContext(AgentSession session)
    {
        #region Utilities
        List<QuickReply>? GetQuickReplies(string quickRepliesJSON)
        {
            try
            {
                List<QuickReply>? quickReplies = JsonSerializer.Deserialize<List<QuickReply>>(quickRepliesJSON, Records.DefaultJsonSerializerOptions);
                if (quickReplies is { Count: > 0 })
                {
                    agentLogger.LogInformation("Retrieved {QuickRepliesCount} quick replies from context", quickReplies.Count);
                    return quickReplies;
                }
            }
            catch (JsonException ex)
            {
                agentLogger.LogError(ex, "Failed to deserialize quick replies from context");
                aiContextProvider.DropVariable(session, Constants.ContextKeys.QuickReplies);
            }

            return null;
        }
        #endregion

        object? ctxQuickReplies = aiContextProvider.GetVariable(session, Constants.ContextKeys.QuickReplies);
        return ctxQuickReplies switch
        {
            string ctxQuickRepliesJson when !string.IsNullOrEmpty(ctxQuickRepliesJson) => GetQuickReplies(ctxQuickRepliesJson),
            JsonElement { ValueKind: JsonValueKind.String } ctxQuickRepliesJsonElement => GetQuickReplies(ctxQuickRepliesJsonElement.GetString()!),
            _ => null
        };
    }

    /// <summary>
    /// Reads and deserializes the <c>rich_card</c> context variable, if the agent set one
    /// on the current turn via the <c>SetRichCard</c> base tool. Drops the variable if the
    /// stored JSON is malformed.
    /// </summary>
    /// <param name="session">Active agent session.</param>
    /// <returns>The deserialized rich card, or <c>null</c> if absent or invalid.</returns>
    protected RichCard? GetRichCardFromContext(AgentSession session)
    {
        #region Utilities
        RichCard? GetRichCard(string richCardJSON)
        {
            try
            {
                RichCard? richCard = JsonSerializer.Deserialize<RichCard>(
                    richCardJSON, Records.DefaultJsonSerializerOptions);
                if (richCard != null)
                {
                    agentLogger.LogInformation("Retrieved rich card from context");
                    return richCard;
                }
            }
            catch (JsonException ex)
            {
                agentLogger.LogError(ex, "Failed to deserialize rich card from context");
                aiContextProvider.DropVariable(session, Constants.ContextKeys.RichCard);
            }

            return null;
        }
        #endregion

        object? ctxRichCard = aiContextProvider.GetVariable(session, Constants.ContextKeys.RichCard);
        return ctxRichCard switch
        {
            string ctxRichCardJson when !string.IsNullOrEmpty(ctxRichCardJson) => GetRichCard(ctxRichCardJson),
            JsonElement { ValueKind: JsonValueKind.String } ctxRichCardJsonElement => GetRichCard(ctxRichCardJsonElement.GetString()!),
            _ => null
        };
    }
}