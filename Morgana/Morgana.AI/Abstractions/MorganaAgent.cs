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
/// <para>Each concrete agent subclass handles one intent. Providers (<see cref="MorganaAIContextProvider"/>,
/// <see cref="MorganaChatHistoryProvider"/>) are singletons attached to the <see cref="AIAgent"/> instance
/// and shared across all sessions. All per-session state lives inside <see cref="AgentSession"/>
/// and is serialized automatically by the framework.</para>
///
/// <para><see cref="CurrentSession"/> is set at the start of each <see cref="ExecuteAgentAsync"/> turn
/// and is the session passed to all provider calls within that turn, including tool invocations.
/// Because <see cref="MorganaAgent"/> is an Akka actor (single-thread message processing),
/// there is no concurrent access to <see cref="CurrentSession"/>.</para>
///
/// <para><strong>OpenTelemetry:</strong> <see cref="ExecuteAgentAsync"/> opens a <c>morgana.agent</c>
/// Activity as child of <c>AgentRequest.TurnContext</c>, emits a <c>first_chunk</c> event on TTFT,
/// and tags the span with <c>agent.ttft_ms</c>, <c>agent.response_preview</c>,
/// <c>agent.is_completed</c>, and <c>agent.has_quick_replies</c>.</para>
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
    /// Persistence service used to load, save, and resume serialized <see cref="AgentSession"/>s
    /// across actor restarts and conversation resumes.
    /// </summary>
    protected readonly IConversationPersistenceService persistenceService;

    /// <summary>
    /// Logger scoped to this agent instance, used for turn-level diagnostics and tool tracing.
    /// </summary>
    protected readonly ILogger agentLogger;

    /// <summary>
    /// The active <see cref="AgentSession"/> for the current turn.
    /// Exposed so that tool closures can pass it to provider calls (GetVariable, SetVariable, etc.).
    /// Always non-null during a live agent invocation.
    /// </summary>
    public AgentSession? CurrentSession => aiAgentSession;

    /// <summary>
    /// Intent name handled by this agent, resolved from the mandatory
    /// <see cref="HandlesIntentAttribute"/> on the concrete subclass.
    /// </summary>
    protected string AgentIntent => GetType().GetCustomAttribute<HandlesIntentAttribute>()?.Intent
                                     ?? throw new InvalidOperationException($"Agent {GetType().Name} must be decorated with [HandlesIntent] attribute");

    /// <summary>
    /// Stable identifier for this agent within a conversation, formatted as
    /// <c>{AgentIntent}-{conversationId}</c>. Used as the persistence key for <see cref="AgentSession"/>.
    /// </summary>
    protected string AgentIdentifier => $"{AgentIntent}-{conversationId}";

    /// <summary>
    /// Initializes the agent actor and wires message handlers for
    /// <see cref="Records.AgentRequest"/> and <see cref="Records.FailureContext"/>.
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

        ReceiveAsync<Records.AgentRequest>(ExecuteAgentAsync);
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
    /// <para>The persistence layer enforces first-write-wins via <c>INSERT OR IGNORE</c>, mirroring
    /// the rule that <see cref="MorganaAIContextProvider.MergeSharedContext"/> applies on the
    /// read side. The call is awaited synchronously inside the actor: SQLite writes are
    /// sub-millisecond on local storage and the actor processes one message at a time anyway.</para>
    /// </remarks>
    /// <param name="key">Name of the shared variable.</param>
    /// <param name="value">Value to persist.</param>
    protected void OnSharedContextUpdate(string key, object value)
    {
        agentLogger.LogInformation("Agent {AgentIntent} writing shared context variable: {Key}", AgentIntent, key);

        // Block the actor briefly to honour first-write-wins ordering: the next inbound message
        // (e.g. another tool call from the same turn) must observe the previous shared write.
        persistenceService.UpsertSharedVariableAsync(conversationId, key, value, AgentIntent)
            .GetAwaiter().GetResult();
    }

    /// <summary>
    /// Processes an incoming <see cref="Records.AgentRequest"/>, running the LLM turn
    /// and streaming or batching the response back to the sender.
    /// </summary>
    /// <param name="req">Agent request containing the user's message, optional classification, and OTel TurnContext</param>
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
            aiAgentSession ??= await persistenceService.LoadAgentConversationAsync(AgentIdentifier, this);
            if (aiAgentSession != null)
            {
                agentLogger.LogInformation("Loaded existing conversation session for {AgentIdentifier}", AgentIdentifier);

                agentSpan?.AddEvent(new ActivityEvent(MorganaTelemetry.ResumeAgentConversation));
            }
            else
            {
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
                aiContextProvider.MergeSharedContext(aiAgentSession, sharedFromRegistry);
            }

            StringBuilder fullResponse = new StringBuilder();
            ChatMessage userMessage = new ChatMessage(ChatRole.User, req.Content!) { CreatedAt = DateTimeOffset.UtcNow };

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

            if (useStreaming)
            {
                Stopwatch firstChunkStopwatch = Stopwatch.StartNew();
                bool firstChunkEmitted = false;

                await foreach (AgentResponseUpdate chunk in aiAgent.RunStreamingAsync(userMessage, aiAgentSession))
                {
                    if (!string.IsNullOrEmpty(chunk.Text))
                    {
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
                AgentResponse response = await aiAgent.RunAsync(userMessage, aiAgentSession);
                responseStopwatch.Stop();
                fullResponse.Append(response.Text);

                long ttft = responseStopwatch.ElapsedMilliseconds;
                agentSpan?.AddEvent(new ActivityEvent(MorganaTelemetry.EventFirstChunk));
                agentSpan?.SetTag(MorganaTelemetry.AgentTtftMs, ttft);
                MorganaTelemetry.AgentTtftHistogram.Record(ttft);
            }

            string llmResponseText = fullResponse.ToString();
            bool hasInteractiveToken = llmResponseText.Contains("#INT#", StringComparison.OrdinalIgnoreCase);
            bool endsWithQuestion = llmResponseText.EndsWith('?');

            #region LLM tools
            List<QuickReply>? quickReplies = GetQuickRepliesFromContext(aiAgentSession);
            bool hasQuickReplies = quickReplies?.Count > 0;

            RichCard? richCard = GetRichCardFromContext(aiAgentSession);
            bool hasRichCard = richCard != null;

            if (hasQuickReplies)
            {
                aiContextProvider.DropVariable(aiAgentSession, "quick_replies");
                agentLogger.LogInformation("Dropped {Count} quick replies from context (ephemeral data)", quickReplies!.Count);
            }

            if (hasRichCard)
            {
                aiContextProvider.DropVariable(aiAgentSession, "rich_card");
                agentLogger.LogInformation("Dropped rich card '{Title}' from context (ephemeral data)", richCard!.Title);
            }
            #endregion

            bool isCompleted = !hasInteractiveToken && !endsWithQuestion && !hasQuickReplies && !hasRichCard;

            agentLogger.LogInformation(
                $"Agent response analysis: HasINT={hasInteractiveToken}," +
                $"EndsWithQuestion={endsWithQuestion}," +
                $"HasQuickReplies={hasQuickReplies}," +
                $"HasRichCard={hasRichCard}," +
                $"IsCompleted={isCompleted}");

            // Finalize agent span with outcome attributes
            string responsePreview = llmResponseText.Length > 150 ? llmResponseText[..150] : llmResponseText;
            agentSpan?.SetTag(MorganaTelemetry.AgentIsCompleted, isCompleted);
            agentSpan?.SetTag(MorganaTelemetry.AgentHasQuickReplies, hasQuickReplies);
            agentSpan?.SetTag(MorganaTelemetry.AgentResponsePreview, responsePreview);
            agentSpan?.Dispose();

            // Tag this turn's user-facing assistant message — the LAST assistant message that
            // actually carries text content. The Anthropic / OpenAI / etc. tool-use protocol
            // persists one assistant message per tool-calling round (text + tool_call →
            // tool_result → text + tool_call → ... → final). The visible answer can land on the
            // very last assistant or, for some models (e.g. Haiku 4.5 after a closing tool_result),
            // on a slightly earlier one with the trailing assistant left empty. Picking the last
            // assistant *with text* covers both layouts: an empty trailing assistant is skipped,
            // and the marker sits on the message whose text is what the user actually saw live.
            ChatMessage? finalAssistantMessage = aiChatHistoryProvider
                .GetMessages(aiAgentSession)
                .Where(m => m.Role == ChatRole.Assistant
                         && m.Contents.OfType<TextContent>().Any(t => !string.IsNullOrWhiteSpace(t.Text)))
                .LastOrDefault();
            if (finalAssistantMessage is not null)
            {
                finalAssistantMessage.AdditionalProperties ??= new AdditionalPropertiesDictionary();
                finalAssistantMessage.AdditionalProperties[MorganaChatHistoryProvider.UserFacingMarkerKey] = true;
            }

            await persistenceService.SaveAgentConversationAsync(AgentIdentifier, aiAgent, aiAgentSession, isCompleted);
            agentLogger.LogInformation("Saved conversation state for {AgentIdentifier}", AgentIdentifier);

#if DEBUG
            senderRef.Tell(new Records.AgentResponse(llmResponseText, isCompleted, quickReplies, richCard));
#else
            senderRef.Tell(new Records.AgentResponse(llmResponseText.Replace("#INT#", "", StringComparison.OrdinalIgnoreCase).Trim(), isCompleted, quickReplies, richCard));
#endif
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
            // Safety net: ephemeral UI variables (rich card, quick replies) must NEVER leak
            // to the next turn. The happy path drops them above after reading, but if the LLM
            // populated them via SetRichCard/SetQuickReplies tool calls and then the stream
            // or the save threw, the happy-path drop is skipped and the stale values would be
            // picked up by GetRichCardFromContext/GetQuickRepliesFromContext on the next turn.
            // DropVariable is idempotent (no-op when the key is absent), so this is safe even
            // after a successful path.
            if (aiAgentSession is not null)
            {
                aiContextProvider.DropVariable(aiAgentSession, "rich_card");
                aiContextProvider.DropVariable(aiAgentSession, "quick_replies");
            }
        }
    }

    private async Task HandleAgentFailureAsync(Records.FailureContext failure)
    {
        agentLogger.LogError(failure.Failure.Cause, "Agent execution failed in {Name}", GetType().Name);

        Records.Prompt morganaPrompt = await promptResolverService.ResolveAsync("Morgana");
        List<Records.ErrorAnswer> errorAnswers = morganaPrompt.GetAdditionalProperty<List<Records.ErrorAnswer>>("ErrorAnswers");
        Records.ErrorAnswer? genericError = errorAnswers.FirstOrDefault(e => string.Equals(e.Name, "GenericError", StringComparison.OrdinalIgnoreCase));

        failure.OriginalSender.Tell(new Records.AgentResponse(genericError?.Content ?? "An internal error occurred.", true, null));
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
                aiContextProvider.DropVariable(session, "quick_replies");
            }

            return null;
        }
        #endregion

        object? ctxQuickReplies = aiContextProvider.GetVariable(session, "quick_replies");
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
                aiContextProvider.DropVariable(session, "rich_card");
            }

            return null;
        }
        #endregion

        object? ctxRichCard = aiContextProvider.GetVariable(session, "rich_card");
        return ctxRichCard switch
        {
            string ctxRichCardJson when !string.IsNullOrEmpty(ctxRichCardJson) => GetRichCard(ctxRichCardJson),
            JsonElement { ValueKind: JsonValueKind.String } ctxRichCardJsonElement => GetRichCard(ctxRichCardJsonElement.GetString()!),
            _ => null
        };
    }
}
