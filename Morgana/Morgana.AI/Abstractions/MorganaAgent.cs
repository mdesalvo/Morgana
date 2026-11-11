using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Akka.Actor;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Morgana.AI.Adapters;
using Morgana.AI.Attributes;
using Morgana.AI.Interfaces;
using Morgana.AI.Providers;
using Morgana.Contracts;
using Status = Akka.Actor.Status;

namespace Morgana.AI.Abstractions;

/// <summary>Base of every domain agent: the actor that runs one intent's model, session and tools for one conversation.</summary>
// The context and history providers live on the AIAgent and are shared by its sessions, while each session's own state
// travels in the AgentSession. The actor runs one turn at a time, which is what lets CurrentSession be a plain field.
public class MorganaAgent : MorganaActor
{
    /// <summary>The Microsoft.Agents.AI agent that talks to the model for this actor, kept for the actor's life.</summary>
    private readonly AIAgent aiAgent;

    /// <summary>The agent's own session in the conversation, loaded from the record on its first turn; null before it.</summary>
    protected AgentSession? aiAgentSession;

    /// <summary>Holds the session's context variables for the tools to read and write, forwarding the shared ones to the conversation's registry.</summary>
    private readonly MorganaAIContextProvider aiContextProvider;

    /// <summary>Holds the messages of a session, which every turn appends to and reads back from.</summary>
    private readonly MorganaChatHistoryProvider aiChatHistoryProvider;

    /// <summary>The conversation's record, where the session is loaded from and saved to around every turn.</summary>
    protected readonly IConversationPersistenceService persistenceService;

    /// <summary>The concrete agent's logger.</summary>
    protected readonly ILogger agentLogger;

    /// <summary>The throwaway session that a colleague's question is answered on; null outside that turn.</summary>
    private AgentSession? consultationSession;

    /// <summary>The session that the running turn works on, which the tools hand to the context provider; never null during a turn.</summary>
    // While a colleague is being answered this is the consultation's own session: that turn's tools and the guard refusing a
    // chained consultation all read the session the turn actually runs on.
    public AgentSession? CurrentSession
        => consultationSession ?? aiAgentSession;

    /// <summary>The intent that this agent handles, as the concrete class's <see cref="HandlesIntentAttribute"/> declares it.</summary>
    protected string AgentIntent
        // The registry refuses at startup an agent without the attribute, so the throw guards a class built outside it.
        => GetType().GetCustomAttribute<HandlesIntentAttribute>()?.Intent
            ?? throw new InvalidOperationException($"Agent {GetType().Name} must be decorated with [HandlesIntent] attribute");

    /// <summary>The agent's identifier in the conversation, <c>{intent}-{conversationId}</c>, under which its session is saved.</summary>
    protected string AgentIdentifier
        // The same form names the actor and the row, so the record of a turn is found from the actor that ran it.
        => $"{AgentIntent}-{conversationId}";

    /// <summary>Builds the agent of the concrete class and wires the three messages the actor answers.</summary>
    /// <param name="conversationId">The conversation this agent serves.</param>
    /// <param name="llmService">The language models, on the tier the concrete class declares.</param>
    /// <param name="promptResolverService">Resolves the framework and domain prompts.</param>
    /// <param name="persistenceService">The conversation's record.</param>
    /// <param name="agentLogger">The concrete agent's logger.</param>
    /// <param name="morganaAgentAdapter">Builds the agent this actor runs, with its tools, colleagues and guards, from the concrete agent's own attributes.</param>
    /// <param name="configuration">The host's configuration, read for the streaming switch.</param>
    public MorganaAgent(
        string conversationId,
        ILanguageModelService llmService,
        IPromptResolverService promptResolverService,
        IConversationPersistenceService persistenceService,
        ILogger agentLogger,
        MorganaAgentAdapter morganaAgentAdapter,
        IConfiguration configuration) : base(conversationId, llmService, promptResolverService, configuration)
    {
        // The record and the logger are the concrete agent's, handed down by its own constructor.
        this.persistenceService = persistenceService;
        this.agentLogger = agentLogger;

        // The agent of the concrete class is built here (its attributes name intent, tier, tools and
        // colleagues) so every field is assigned before the actor receives a message and no subclass
        // has to remember to do it.
        Records.CreatedAgent createdAgent = morganaAgentAdapter.CreateAgent(GetType(), conversationId, () => CurrentSession, OnSharedContextUpdate);
        aiAgent = createdAgent.Agent;
        aiContextProvider = createdAgent.ContextProvider;
        aiChatHistoryProvider = createdAgent.HistoryProvider;

        // The user's turn, dispatched by the router once the supervisor has routed the intent here.
        ReceiveAsync<Records.AgentRequest>(ExecuteAgentAsync);

        // A colleague's question, arriving from MorganaHostedAgent over A2A. It never passes through
        // the supervisor, so it is the one turn this actor runs outside the conversation's pipeline.
        ReceiveAsync<Records.PeerConsultation>(ServeConsultationAsync);

        // A turn that threw, sent to Self so the answer to the waiting sender is composed on the
        // actor's own thread rather than inside the catch that could not produce one.
        ReceiveAsync<Records.FailureContext>(HandleAgentFailureAsync);
    }

    /// <summary>Restores a saved session, history and context state, as the agent's own.</summary>
    public virtual async Task<AgentSession> DeserializeSessionAsync(
        JsonElement serializedState,
        JsonSerializerOptions? jsonSerializerOptions = null)
    {
        // What comes back is the agent's own session from here on: the callback rewired below and
        // every later turn, act on this instance rather than on whatever the actor held before.
        aiAgentSession = await aiAgent.DeserializeSessionAsync(serializedState, jsonSerializerOptions);

        // The callback that forwards shared variables to the registry is not serialized, so it is wired again here.
        aiContextProvider.OnSharedContextUpdate = OnSharedContextUpdate;

        agentLogger.LogInformation("Deserialized AgentSession for conversation {ConversationId}", conversationId);

        // The persistence service hands the restored session back to the turn that asked for it.
        return aiAgentSession;
    }

    /// <summary>
    /// Writes a shared context variable to the conversation's registry, where every agent reads it at the start of its next turn.
    /// Written once and read on demand: an agent that never runs in the conversation pays nothing for it.
    /// </summary>
    /// <param name="key">The variable's name.</param>
    /// <param name="value">The variable's value.</param>
    protected async Task OnSharedContextUpdate(string key, object value)
    {
        // The write of a turn served for a colleague dies with the ephemeral session it was made on,
        // exactly like everything else that turn wrote. Were it let through, first-write-wins would
        // make a value nobody ever confirmed to the user binding on every agent for the rest of the
        // conversation: an exchange that leaves no trace would legislate for the whole shop.
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

    /// <summary>Runs the user's turn on the model, forwarding its text as it streams where the channel shows chunks.</summary>
    /// <param name="req">The user's message with its classification, the channel's capabilities and the turn's trace context.</param>
    protected virtual async Task ExecuteAgentAsync(Records.AgentRequest req)
    {
        // Whoever dispatched the turn, captured now because Sender changes once the actor awaits.
        IActorRef? senderRef = Sender;

        // The morgana.agent span is a child of the turn span that the supervisor propagated and stays open for the
        // whole stream, so the time to the first token can be recorded on it.
        Activity? agentSpan = Telemetry.Source.StartActivity(
            Telemetry.AgentActivity,
            ActivityKind.Internal,
            req.TurnContext);

        // The span names the conversation and the agent, so a trace can be filtered by either.
        agentSpan?.SetTag(Telemetry.ConversationId, conversationId);
        agentSpan?.SetTag(Telemetry.AgentName, GetType().Name);
        agentSpan?.SetTag(Telemetry.AgentIntent, AgentIntent);

        // Every update of the run, which is what a stopped turn keeps its tool calls from.
        List<AgentResponseUpdate> turnUpdates = [];

        // History length before the turn, set once the session is loaded.
        int historyBaseline = 0;

        try
        {
            // Read from the database on first activation and whenever the row was rewritten since this actor read it.
            // Otherwise the field holds the live session, which is exactly what the row says at a turn's start.
            // The agent hands itself over because deserializing a session is its own responsibility.
            if (aiAgentSession is null || await persistenceService.IsDirtyAsync(AgentIdentifier))
            {
                // A row that does not exist yet comes back as null and is created below.
                aiAgentSession = await persistenceService.LoadAgentConversationAsync(AgentIdentifier, this);

                // Reported only when the record was actually read: a turn served from the live session resumes nothing
                if (aiAgentSession != null)
                {
                    agentLogger.LogInformation("Loaded existing conversation session for {AgentIdentifier}", AgentIdentifier);

                    agentSpan?.AddEvent(new ActivityEvent(Telemetry.ResumeAgentConversation));
                }
            }

            // Still nothing after the read: the agent has never run in this conversation.
            if (aiAgentSession is null)
            {
                // No row under this identifier: first time this agent is activated in the conversation.
                // It starts with an empty history: the shared registry below is all it inherits.
                aiAgentSession = await aiAgent.CreateSessionAsync();

                agentLogger.LogInformation("Created new conversation session for {AgentIdentifier}", AgentIdentifier);

                agentSpan?.AddEvent(new ActivityEvent(Telemetry.CreateAgentConversation));
            }

            // The identifier is on record by now, whichever way the session was obtained.
            agentSpan?.SetTag(Telemetry.AgentIdentifier, AgentIdentifier);

            // The shared variables that any agent of the conversation wrote, alive or not, are pulled at turn start.
            // The first value wins twice: the registry ignores a later write of a name and the merge keeps
            // what the agent's own session already holds.
            Dictionary<string, object> sharedFromRegistry = await persistenceService.LoadSharedVariablesAsync(conversationId);

            // An empty registry leaves the session as it is.
            if (sharedFromRegistry.Count > 0)
            {
                agentLogger.LogInformation(
                    "Agent '{AgentIntent}' hydrating {Count} shared variable(s) from registry: {Keys}",
                    AgentIntent, sharedFromRegistry.Count, string.Join(", ", sharedFromRegistry.Keys));

                // Merged into the agent's own session, so the values survive the turn and are persisted
                // with it, unlike a consultation, which merges the same registry into a session that dies.
                aiContextProvider.MergeSharedContext(aiAgentSession, sharedFromRegistry);
            }

            // Stamped server-side because the history of a conversation is reassembled by merging every
            // agent's own session chronologically: a message without a timestamp cannot be placed.
            ChatMessage userMessage = new ChatMessage(ChatRole.User, req.Content!) { CreatedAt = DateTimeOffset.UtcNow };

            // A phrase that arrived while no agent was active is already saved as Morgana's own.
            // This agent keeps it because its model has to read it, marked as somebody else's so a
            // transcript shows the user their own words once.
            if (req.ContentAlreadyStored)
            {
                // The mark travels in the message's own properties, which the history endpoint reads back.
                userMessage.AdditionalProperties ??= new AdditionalPropertiesDictionary();
                userMessage.AdditionalProperties[Constants.MessageProperties.ContextOnly] = true;
            }

            // History length before this turn runs: everything appended past this point belongs to
            // the turn, which is what agent.tools_invoked must report: the span is per turn, so
            // reporting the whole session would attribute every past tool call to this one.
            historyBaseline = aiChatHistoryProvider.GetMessages(aiAgentSession).Count;

            // The phrase enters the session before a single token is spent on it. The turn below runs
            // on the session alone, so nothing reaches the model twice. Written out too, because
            // when this agent was already active nobody else saved the phrase: its own row is the
            // only record. A client reloading mid-turn reads it back from here instead of
            // watching it vanish. The row is left active on purpose: a turn interrupted here resumes
            // at the agent that was working on it.
            aiChatHistoryProvider.AppendMessage(aiAgentSession, userMessage);
            await persistenceService.SaveAgentConversationAsync(AgentIdentifier, aiAgent, aiAgentSession, isCompleted: false);

            // A landing is the one turn in which the agent opens its part of the conversation: the mark is set after
            // the save above, so a turn interrupted here never leaves a row that would land again on resume.
            if (req.IsLanding)
                await aiContextProvider.SetVariableAsync(aiAgentSession, Constants.ContextKeys.Landing, true);

            // The model is always read as a stream, because the updates are what a stopped turn keeps.
            // The global switch and the channel's capability decide only whether chunks are forwarded;
            // a request carrying no capabilities is assumed to stream.
            bool streamingConfigEnabled = configuration.GetValue("Morgana:AdaptiveMessaging:EnableStreamingResponse", true);
            bool channelSupportsStreaming = req.Capabilities?.SupportsStreaming ?? true;
            bool useStreaming = streamingConfigEnabled && channelSupportsStreaming;

            // A channel that cannot stream is worth a line, because its user waits for the whole answer.
            if (!channelSupportsStreaming)
                agentLogger.LogInformation("Agent '{AgentIntent}' forwarding no chunks: channel does not advertise SupportsStreaming", AgentIntent);

            // The answer assembled across chunks, the clock of the first token and the message the last text belonged to.
            StringBuilder fullResponse = new StringBuilder();
            Stopwatch firstChunkStopwatch = Stopwatch.StartNew();
            bool firstChunkEmitted = false;
            string? lastTextMessageId = null;

            // The whole turn runs here, tool calls included, which surface as chunks carrying no text.
            // The phrase being answered is the one already filed above, so nothing is handed in here.
            // The run stops when the supervisor stops waiting for it.
            await foreach (AgentResponseUpdate chunk in aiAgent.RunStreamingAsync(aiAgentSession, cancellationToken: req.TurnCancellation))
            {
                // Kept whole so that a stopped turn can rebuild the tool calls that already ran.
                turnUpdates.Add(chunk);

                // A chunk with text is part of the answer; one without is the turn working on something else.
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
                        if (useStreaming)
                            senderRef.Tell(new Records.AgentStreamChunk(Constants.Markers.MessageSeparator));
                    }

                    // The next boundary is measured from the message this text belongs to.
                    lastTextMessageId = chunk.MessageId;

                    // The text joins the answer and, where the channel streams, reaches the user at once.
                    fullResponse.Append(chunk.Text);
                    if (useStreaming)
                        senderRef.Tell(new Records.AgentStreamChunk(chunk.Text));

                    // The time to the first token is taken once, on the first chunk that carries text.
                    if (!firstChunkEmitted)
                    {
                        firstChunkEmitted = true;
                        long timeToFirstTokenMilliseconds = firstChunkStopwatch.ElapsedMilliseconds;
                        firstChunkStopwatch.Stop();
                        agentSpan?.AddEvent(new ActivityEvent(Telemetry.EventFirstChunk));
                        agentSpan?.SetTag(Telemetry.AgentTtftMs, timeToFirstTokenMilliseconds);
                        Telemetry.AgentTtftHistogram.Record(timeToFirstTokenMilliseconds);
                    }
                }
                else if (useStreaming)
                {
                    // The turn is advancing on something with no text in it, a tool being called
                    // or a colleague being asked. The supervisor's wait counts silence and a
                    // consultation is a whole turn at another agent: unannounced, it reads as an
                    // agent that has died and the user's turn is abandoned while its answer is
                    // still being written.
                    senderRef.Tell(new Records.AgentStillWorking());
                }
            }

            // The answer as the user receives it, without the whitespace that chunks leave at its edges.
            string llmResponseText = fullResponse.ToString().Trim();

            // Whether the agent called SetTurnEnding this turn, read once and dropped so it never reaches the next.
            bool wantsEnding = GetTurnEndingFromContext(aiAgentSession);
            aiContextProvider.DropVariable(aiAgentSession, Constants.ContextKeys.TurnEnding);

            // The landing mark lasts this turn alone and must not be saved with the session below.
            aiContextProvider.DropVariable(aiAgentSession, Constants.ContextKeys.Landing);

            // The buttons that SetQuickReplies attached to this answer, handed to the supervisor and dropped from the session.
            List<QuickReply>? quickReplies = GetQuickRepliesFromContext(aiAgentSession);
            bool hasQuickReplies = quickReplies?.Count > 0;
            if (hasQuickReplies)
            {
                aiContextProvider.DropVariable(aiAgentSession, Constants.ContextKeys.QuickReplies);
                agentLogger.LogInformation("Dropped {Count} quick replies from context (ephemeral data)", quickReplies!.Count);
            }

            // The card that SetRichCard attached to this answer, handed over and dropped the same way.
            RichCard? richCard = GetRichCardFromContext(aiAgentSession);
            bool hasRichCard = richCard != null;
            if (hasRichCard)
            {
                aiContextProvider.DropVariable(aiAgentSession, Constants.ContextKeys.RichCard);
                agentLogger.LogInformation("Dropped rich card '{Title}' from context (ephemeral data)", richCard!.Title);
            }

            // The agent stays in service unless it hands the conversation back. A turn offering buttons
            // or a card awaits the answer to them whatever it declared.
            bool isCompleted = wantsEnding && !hasQuickReplies && !hasRichCard;

            agentLogger.LogInformation(
                "Agent response analysis: WantsEnding={WantsEnding}, HasQuickReplies={HasQuickReplies}, HasRichCard={HasRichCard}, IsCompleted={IsCompleted}",
                wantsEnding, hasQuickReplies, hasRichCard, isCompleted);

            // The span closes on the turn's outcome: completion, buttons, the tools called and the start of the answer.
            string responsePreview = Preview(llmResponseText);
            agentSpan?.SetTag(Telemetry.AgentIsCompleted, isCompleted);
            agentSpan?.SetTag(Telemetry.AgentHasQuickReplies, hasQuickReplies);
            agentSpan?.SetTag(Telemetry.AgentToolsInvoked, GetToolsInvoked(aiAgentSession, historyBaseline));
            agentSpan?.SetTag(Telemetry.AgentResponsePreview, responsePreview);
            agentSpan?.Dispose();

            // The exchange with a colleague is spent once it has been read. Clearing it here keeps
            // it out of the caller's own session (see StripPeerConsultations) and is done after
            // the span has been tagged, so telemetry still records that the colleague was consulted.
            StripPeerConsultations(aiAgentSession, historyBaseline);

            // The turn's answer to the user is its last assistant message carrying text, marked for the history endpoint.
            ChatMessage? finalAssistantMessage = aiChatHistoryProvider
                .GetMessages(aiAgentSession)
                .LastOrDefault(m => m.Role == ChatRole.Assistant
                                     && m.Contents.OfType<TextContent>().Any(t => !string.IsNullOrWhiteSpace(t.Text)));

            // A turn that wrote no text has no answer to mark.
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

            // Dated as the history keeps it, so a client catching up recognises the reply it was pushed
            senderRef.Tell(new Records.AgentResponse(
                llmResponseText, isCompleted, quickReplies, richCard, finalAssistantMessage?.CreatedAt?.UtcDateTime));
        }
        // The supervisor stopped waiting: the run was cut and what it had already done is kept.
        catch (OperationCanceledException) when (req.TurnCancellation.IsCancellationRequested)
        {
            agentLogger.LogWarning("Turn of agent {AgentIntent} stopped for conversation {ConversationId}: the supervisor no longer waits for it", AgentIntent, conversationId);

            // A turn stopped before its session was loaded has nothing to keep.
            if (aiAgentSession is not null)
            {
                // The calls whose tool answered before the stop: the only ones a provider accepts in a history.
                HashSet<string> answeredCallIds = [.. turnUpdates.SelectMany(update => update.Contents.OfType<FunctionResultContent>()).Select(result => result.CallId)];

                // The updates rebuilt into the messages the run had produced when it was cut.
                foreach (ChatMessage interruptedMessage in turnUpdates.ToAgentResponse().Messages)
                {
                    // The tool calls that ran are facts and stay with their results; the text is dropped because the
                    // channel replaces what was streamed with Morgana's apology, so the next turn must not read as
                    // said what the user never saw; a call whose tool never answered would be refused by the provider.
                    List<AIContent> keptContents = [.. interruptedMessage.Contents.Where(content => content is FunctionResultContent || content is FunctionCallContent call && answeredCallIds.Contains(call.CallId))];

                    // A message that held only text has nothing left to keep.
                    if (keptContents.Count == 0)
                        continue;

                    // The message keeps only the facts of the calls that ran.
                    interruptedMessage.Contents = keptContents;

                    // Morgana's clock, which the history orders by, as on a completed turn.
                    interruptedMessage.CreatedAt = DateTimeOffset.UtcNow;

                    // Filed after the user's phrase that the turn answered, where the next turn's model reads it.
                    aiChatHistoryProvider.AppendMessage(aiAgentSession, interruptedMessage);
                }

                // A consultation is cleared from a stopped turn as from a completed one.
                StripPeerConsultations(aiAgentSession, historyBaseline);

                // Dropped before the save, so the row holds none of the turn's ephemeral variables.
                DropTurnVariables(aiAgentSession);

                // The supervisor has dropped this agent for the conversation, so its row leaves active duty too and a restart does not bring it back.
                await persistenceService.SaveAgentConversationAsync(AgentIdentifier, aiAgent, aiAgentSession, isCompleted: true);
            }

            // The turn ended without an answer to give.
            agentSpan?.SetStatus(ActivityStatusCode.Error, "interrupted");
            agentSpan?.Dispose();

            // No reply to senderRef: the supervisor already answered the user and a late answer would be read as the answer of a later turn.
        }
        // The provider of the agent's tier decides whether the failure is its own content refusal; the registry
        // refuses at startup an agent without [RequiresLLMTier], so the tier is always declared.
        catch (Exception ex) when (llmService.GetLanguageModel(GetType().GetCustomAttribute<RequiresLLMTierAttribute>()!.Tier).IsContentFilterRefusal(ex))
        {
            agentLogger.LogWarning(ex, "Content filter rejection in {Name} for conversation {ConversationId}", GetType().Name, conversationId);
            // The span closes on the provider's refusal of the content.
            agentSpan?.SetStatus(ActivityStatusCode.Error, "content_filter");
            agentSpan?.AddException(ex);
            agentSpan?.Dispose();

            // The supervisor answers the user as it would a guard's rejection.
            senderRef.Tell(new Records.ContentFilterRejection());
        }
        catch (Exception ex)
        {
            agentLogger.LogError(ex, "Error in {Name}", GetType().Name);

            // The span closes on the failure that ended the turn.
            agentSpan?.SetStatus(ActivityStatusCode.Error, ex.Message);
            agentSpan?.AddException(ex);
            agentSpan?.Dispose();

            // The apology is composed on the actor's own thread, from a message to Self, rather than inside this catch.
            Self.Tell(new Records.FailureContext(new Status.Failure(ex), senderRef));
        }
        finally
        {
            // Whatever path the turn took, the variables that live for one turn never reach the next.
            if (aiAgentSession is not null)
                DropTurnVariables(aiAgentSession);
        }
    }

    /// <summary>Answers a colleague's question with a full turn on the model, whose reader is another agent instead of the user.</summary>
    protected virtual async Task ServeConsultationAsync(Records.PeerConsultation consultation)
    {
        // The hosted agent waiting on the answer, captured now because Sender changes once the actor awaits.
        IActorRef senderRef = Sender;

        // Parented on the caller's turn context, which travelled with the question: the answer shows in
        // the trace nested under the work of the agent that asked, not as a turn of its own.
        Activity? consultationSpan = Telemetry.Source.StartActivity(
            Telemetry.ConsultationActivity,
            ActivityKind.Internal,
            consultation.TurnContext);

        // The span names who asked whom and what, so the exchange can be read from the trace alone.
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

            // An empty registry leaves the session as it was created.
            if (sharedFromRegistry.Count > 0)
                aiContextProvider.MergeSharedContext(consultationSession, sharedFromRegistry);

            // Marks this turn as served for a colleague. The adapter's consultation guard reads it to refuse a chained
            // consultation and reads its presence alone, so the mark is a flag and not the caller's name, which a caller
            // that is not an agent of this installation never sent. Who asked is on the span and in the line below.
            await aiContextProvider.SetVariableAsync(consultationSession, Constants.ContextKeys.ServingConsultation, true);

            agentLogger.LogInformation(
                "Agent '{AgentIntent}' is answering a consultation from '{CallerIntent}'", AgentIntent, consultation.CallerIntent);

            // The colleague's question enters as a user message because that is the only role a turn can
            // start from; who really asked is carried by the declaration spliced in front of it. Never
            // streamed: the reader is an agent waiting for one whole answer, not a channel.
            AgentResponse response = await aiAgent.RunAsync(
                new ChatMessage(ChatRole.User, consultation.Question) { CreatedAt = DateTimeOffset.UtcNow },
                consultationSession,
                cancellationToken: consultation.TurnCancellation);

            // The colleague's presentation decisions are handed over as data rather than drained:
            // the asking agent reads the options it was offered and may come back having chosen one.
            // Options are the only way an answer awaits a reply: a colleague answers one question on
            // a session made for it, so its staying in service has nobody to wait for.
            List<QuickReply>? quickReplies = GetQuickRepliesFromContext(consultationSession);
            bool awaitsReply = quickReplies?.Count > 0;
            RichCard? richCard = GetRichCardFromContext(consultationSession);

            // A baseline of 0 where a user turn passes its own: this session was created for the
            // exchange and holds nothing else, so every tool call in it belongs to this answer and
            // there is no earlier history to skip past.
            consultationSpan?.SetTag(Telemetry.ConsultationAwaitingReply, awaitsReply);
            consultationSpan?.SetTag(Telemetry.AgentToolsInvoked, GetToolsInvoked(consultationSession, 0));
            consultationSpan?.SetTag(Telemetry.ConsultationAnswer, response.Text);
            consultationSpan?.Dispose();

            // The answer goes back with nothing persisted, by design: what this turn wrote dies with the session it wrote it
            // on and the agent's own row, if it has one, is left exactly as the user's last turn left it.
            senderRef.Tell(new Records.PeerConsultationResponse(
                response.Text.Trim(),
                awaitsReply,
                quickReplies,
                richCard));
        }
        catch (OperationCanceledException) when (consultation.TurnCancellation.IsCancellationRequested)
        {
            agentLogger.LogInformation("Agent '{AgentIntent}' stopped answering a consultation from '{CallerIntent}': the asker no longer waits", AgentIntent, consultation.CallerIntent);

            // The consultation ended without an answer to give.
            consultationSpan?.SetStatus(ActivityStatusCode.Error, "interrupted");
            consultationSpan?.Dispose();

            // No Tell: the asker has stopped waiting and nothing is persisted on this session by design.
        }
        catch (Exception ex)
        {
            agentLogger.LogError(
                ex, "Agent '{AgentIntent}' failed to answer a consultation from '{CallerIntent}'", AgentIntent, consultation.CallerIntent);

            // The span closes on the failure that ended the consultation.
            consultationSpan?.SetStatus(ActivityStatusCode.Error, ex.Message);
            consultationSpan?.AddException(ex);
            consultationSpan?.Dispose();

            // The asker reads a plain answer in the usual envelope and proceeds without this colleague.
            senderRef.Tell(new Records.PeerConsultationResponse(
                $"Your colleague for '{AgentIntent}' could not answer. Proceed without them.", false));
        }
        finally
        {
            // The failure path included: with the field cleared nothing holds a reference to that
            // session any more and CurrentSession points back at the agent's own. The keys this
            // turn wrote need no dropping: they were written on a session now unreachable.
            consultationSession = null;
        }
    }

    /// <summary>Answers the sender of a failed turn with Morgana's apology, closing the agent's part of the conversation.</summary>
    private async Task HandleAgentFailureAsync(Records.FailureContext failure)
    {
        agentLogger.LogError(failure.Failure.Cause, "Agent execution failed in {Name}", GetType().Name);

        // The apology is authored in Morgana's voice among the ErrorAnswers of her prompt.
        Records.Prompt morganaPrompt = await promptResolverService.ResolveAsync(Constants.Morgana);
        List<Records.ErrorAnswer> errorAnswers = morganaPrompt.GetAdditionalProperty<List<Records.ErrorAnswer>>(Constants.PromptProperties.ErrorAnswers);
        Records.ErrorAnswer? genericError = errorAnswers.FirstOrDefault(e => string.Equals(e.Name, Constants.ErrorAnswers.GenericError, StringComparison.OrdinalIgnoreCase));

        // A completed answer, so the conversation returns to Morgana instead of staying with an agent that failed.
        failure.OriginalSender.Tell(new Records.AgentResponse(genericError?.Content ?? "An internal error occurred.", true, null));
    }

    /// <summary>Drops the variables that live for one turn so that they never reach the next.</summary>
    /// <param name="session">The session whose context holds the turn's variables.</param>
    private void DropTurnVariables(AgentSession session)
    {
        // Rich card and quick replies belong to the answer just given and would otherwise be shown again.
        aiContextProvider.DropVariable(session, Constants.ContextKeys.RichCard);
        aiContextProvider.DropVariable(session, Constants.ContextKeys.QuickReplies);

        // The turn's own verdict on whether the agent hands the conversation back.
        aiContextProvider.DropVariable(session, Constants.ContextKeys.TurnEnding);

        // The rounds of consultation are counted per turn.
        aiContextProvider.DropVariable(session, Constants.ContextKeys.ConsultationRounds);

        // A landing opens one turn alone.
        aiContextProvider.DropVariable(session, Constants.ContextKeys.Landing);

        // The tools withheld by the guard are withheld for this turn alone.
        aiContextProvider.DropVariable(session, Constants.ContextKeys.QuarantinedTools);
    }

    /// <summary>Removes this turn's consultation calls and their results from the agent's own history.</summary>
    /// <param name="session">The session whose history holds the turn just run.</param>
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

    /// <summary>Tells whether <see cref="Constants.Markers.MessageSeparator"/> must come before <paramref name="incoming"/>, which would otherwise weld to the text so far.</summary>
    /// <param name="accumulated">The answer assembled up to this point.</param>
    /// <param name="incoming">The text of the message about to be appended, never empty.</param>
    private static bool NeedsMessageSeparator(StringBuilder accumulated, string incoming)
        // Whitespace on either side already parts the two messages.
        => accumulated.Length > 0
           && !char.IsWhiteSpace(accumulated[^1])
           && !char.IsWhiteSpace(incoming[0]);

    /// <summary>The first 150 characters of <paramref name="text"/>, as much as a span attribute carries.</summary>
    private static string Preview(string? text)
        // Enough for a human reading a trace to recognise what was said, never the whole of it: every attribute reaches every exporter.
        => text is null ? string.Empty : text.Length > 150 ? text[..150] : text;

    /// <summary>The names of the tools that the turn called, in call order and comma-separated, for the <c>agent.tools_invoked</c> span attribute.</summary>
    /// <param name="session">The session the turn ran on.</param>
    /// <param name="historyBaseline">How many messages the history held before the turn ran.</param>
    protected string GetToolsInvoked(AgentSession session, int historyBaseline)
        // Repetitions are kept, since a repeated call is itself a signal; never the arguments, which may carry the user's data.
        => string.Join(", ", aiChatHistoryProvider.GetMessages(session)
            .Skip(historyBaseline)
            .SelectMany(m => m.Contents.OfType<FunctionCallContent>())
            .Select(c => c.Name));

    /// <summary>Tells whether the agent called <c>SetTurnEnding</c> this turn to hand the conversation back to Morgana.</summary>
    /// <param name="session">The session the turn ran on.</param>
    protected bool GetTurnEndingFromContext(AgentSession session)
    {
        // The value that SetTurnEnding wrote, absent when the agent stays in service.
        object? ctxTurnEnding = aiContextProvider.GetFrameworkKeyValue(session, Constants.ContextKeys.TurnEnding);

        // The value arrives typed when written this turn and as JSON or text once restored; anything else is no declaration.
        return ctxTurnEnding switch
        {
            bool ending => ending,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.False } => false,
            JsonElement { ValueKind: JsonValueKind.String } element => bool.TryParse(element.GetString(), out bool parsed) && parsed,
            string text => bool.TryParse(text, out bool parsed) && parsed,
            _ => false
        };
    }

    /// <summary>The buttons that <c>SetQuickReplies</c> attached to this turn's answer; null when there are none or they do not read.</summary>
    /// <param name="session">The session the turn ran on.</param>
    protected List<QuickReply>? GetQuickRepliesFromContext(AgentSession session)
    {
        // The JSON that SetQuickReplies wrote, absent when the answer carries no buttons.
        object? ctxQuickReplies = aiContextProvider.GetFrameworkKeyValue(session, Constants.ContextKeys.QuickReplies);

        // The JSON arrives as text when written this turn and as a JSON string once restored; anything else carries no buttons.
        return ctxQuickReplies switch
        {
            string ctxQuickRepliesJson when !string.IsNullOrEmpty(ctxQuickRepliesJson) => GetQuickReplies(ctxQuickRepliesJson),
            JsonElement { ValueKind: JsonValueKind.String } ctxQuickRepliesJsonElement => GetQuickReplies(ctxQuickRepliesJsonElement.GetString()!),
            _ => null
        };

        #region Utilities
        List<QuickReply>? GetQuickReplies(string quickRepliesJSON)
        {
            try
            {
                // The buttons as the tool serialized them, with the options every record of the wire is read with.
                List<QuickReply>? quickReplies = JsonSerializer.Deserialize<List<QuickReply>>(quickRepliesJSON, Records.DefaultJsonSerializerOptions);

                // An empty list offers nothing, so only a list with buttons is handed on.
                if (quickReplies is { Count: > 0 })
                {
                    agentLogger.LogInformation("Retrieved {QuickRepliesCount} quick replies from context", quickReplies.Count);
                    return quickReplies;
                }
            }
            catch (JsonException ex)
            {
                agentLogger.LogError(ex, "Failed to deserialize quick replies from context");

                // Buttons that do not read would fail again on every later turn, so they are dropped.
                aiContextProvider.DropVariable(session, Constants.ContextKeys.QuickReplies);
            }

            // No buttons to show.
            return null;
        }
        #endregion
    }

    /// <summary>The card that <c>SetRichCard</c> attached to this turn's answer; null when there is none or it does not read.</summary>
    /// <param name="session">The session the turn ran on.</param>
    protected RichCard? GetRichCardFromContext(AgentSession session)
    {
        // The JSON that SetRichCard wrote, absent when the answer carries no card.
        object? ctxRichCard = aiContextProvider.GetFrameworkKeyValue(session, Constants.ContextKeys.RichCard);

        // The JSON arrives as text when written this turn and as a JSON string once restored; anything else carries no card.
        return ctxRichCard switch
        {
            string ctxRichCardJson when !string.IsNullOrEmpty(ctxRichCardJson) => GetRichCard(ctxRichCardJson),
            JsonElement { ValueKind: JsonValueKind.String } ctxRichCardJsonElement => GetRichCard(ctxRichCardJsonElement.GetString()!),
            _ => null
        };

        #region Utilities
        RichCard? GetRichCard(string richCardJSON)
        {
            try
            {
                // The card as the tool serialized it, with the options every record of the wire is read with.
                RichCard? richCard = JsonSerializer.Deserialize<RichCard>(richCardJSON, Records.DefaultJsonSerializerOptions);

                // A JSON null carries no card.
                if (richCard != null)
                {
                    agentLogger.LogInformation("Retrieved rich card from context");
                    return richCard;
                }
            }
            catch (JsonException ex)
            {
                agentLogger.LogError(ex, "Failed to deserialize rich card from context");

                // A card that does not read would fail again on every later turn, so it is dropped.
                aiContextProvider.DropVariable(session, Constants.ContextKeys.RichCard);
            }

            // No card to show.
            return null;
        }
        #endregion
    }
}