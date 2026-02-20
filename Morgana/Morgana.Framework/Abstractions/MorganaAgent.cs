using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Akka.Actor;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Morgana.Framework.Actors;
using Morgana.Framework.Attributes;
using Morgana.Framework.Extensions;
using Morgana.Framework.Interfaces;
using Morgana.Framework.Providers;
using Morgana.Framework.Telemetry;

namespace Morgana.Framework.Abstractions;

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
    protected AIAgent aiAgent;
    protected AgentSession? aiAgentSession;
    protected MorganaAIContextProvider aiContextProvider;
    protected MorganaChatHistoryProvider aiChatHistoryProvider;
    protected readonly IConversationPersistenceService persistenceService;
    protected readonly ILogger agentLogger;

    /// <summary>
    /// Shared context variables received before this agent's first session was established.
    /// Drained and applied at the start of the first <see cref="ExecuteAgentAsync"/> turn.
    /// </summary>
    private readonly List<Dictionary<string, object>> pendingContextMerges = [];

    /// <summary>
    /// The active <see cref="AgentSession"/> for the current turn.
    /// Exposed so that tool closures can pass it to provider calls (GetVariable, SetVariable, etc.).
    /// Always non-null during a live agent invocation.
    /// </summary>
    public AgentSession? CurrentSession => aiAgentSession;

    protected string AgentIntent => GetType().GetCustomAttribute<HandlesIntentAttribute>()?.Intent
                                     ?? throw new InvalidOperationException($"Agent {GetType().Name} must be decorated with [HandlesIntent] attribute");

    protected string AgentIdentifier => $"{AgentIntent}-{conversationId}";

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
        Receive<Records.ReceiveContextUpdate>(HandleContextUpdate);
        ReceiveAsync<Records.FailureContext>(HandleAgentFailureAsync);
    }

    /// <summary>
    /// Restores a serialized <see cref="AgentSession"/>, including conversation history and context state.
    /// After loading, reconnects the <see cref="MorganaAIContextProvider.OnSharedContextUpdate"/> callback
    /// and re-broadcasts shared variables to sibling agents.
    /// </summary>
    public virtual async Task<AgentSession> DeserializeSessionAsync(
        JsonElement serializedState,
        JsonSerializerOptions? jsonSerializerOptions = null)
    {
        aiAgentSession = await aiAgent.DeserializeSessionAsync(serializedState, jsonSerializerOptions);

        // Reconnect the broadcast callback — delegates are not serialized.
        aiContextProvider.OnSharedContextUpdate = OnSharedContextUpdate;

        // Re-broadcast shared variables so sibling agents are up to date.
        aiContextProvider.PropagateSharedVariables(aiAgentSession);

        agentLogger.LogInformation($"Deserialized AgentSession for conversation {conversationId}");

        return aiAgentSession;
    }

    protected void OnSharedContextUpdate(string key, object value)
    {
        agentLogger.LogInformation($"Agent {AgentIntent} broadcasting shared context variable: {key}");

        Context.System
            .ActorSelection($"/user/router-{conversationId}")
            .Tell(new Records.BroadcastContextUpdate(AgentIntent, new Dictionary<string, object> { [key] = value }));
    }

    private void HandleContextUpdate(Records.ReceiveContextUpdate msg)
    {
        agentLogger.LogInformation(
            $"Agent '{AgentIntent}' received shared context from '{msg.SourceAgentIntent}': {string.Join(", ", msg.UpdatedValues.Keys)}");

        if (aiAgentSession is null)
        {
            // No session yet — queue the merge for the next turn.
            pendingContextMerges.Add(msg.UpdatedValues);

            agentLogger.LogInformation(
                $"Agent '{AgentIntent}' queued pending context merge ({pendingContextMerges.Count} total) " +
                $"from '{msg.SourceAgentIntent}': {string.Join(", ", msg.UpdatedValues.Keys)}");
            return;
        }

        aiContextProvider.MergeSharedContext(aiAgentSession, msg.UpdatedValues);
    }

    /// <summary>
    /// Processes an incoming <see cref="Records.AgentRequest"/>, running the LLM turn
    /// and streaming or batching the response back to the sender.
    /// Opens a <c>morgana.agent</c> OTel span as child of <c>AgentRequest.TurnContext</c>.
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
                agentLogger.LogInformation($"Loaded existing conversation session for {AgentIdentifier}");

                agentSpan?.AddEvent(new ActivityEvent(MorganaTelemetry.ResumeAgentConversation));
                agentSpan?.SetTag(MorganaTelemetry.AgentIdentifier, AgentIdentifier);
            }
            else
            {
                aiAgentSession = await aiAgent.CreateSessionAsync();

                agentLogger.LogInformation($"Created new conversation session for {AgentIdentifier}");

                agentSpan?.AddEvent(new ActivityEvent(MorganaTelemetry.CreateAgentConversation));
                agentSpan?.SetTag(MorganaTelemetry.AgentIdentifier, AgentIdentifier);
            }

            // Apply any shared context merges that arrived before the session existed.
            if (pendingContextMerges.Count > 0)
            {
                agentLogger.LogInformation(
                    $"Agent '{AgentIntent}' applying {pendingContextMerges.Count} pending context merge(s)");

                foreach (Dictionary<string, object> pending in pendingContextMerges)
                    aiContextProvider.MergeSharedContext(aiAgentSession, pending);

                pendingContextMerges.Clear();
            }

            StringBuilder fullResponse = new StringBuilder();
            IConfigurationSection config = configuration.GetSection("Morgana:StreamingResponse");
            if (config.GetValue("Enabled", true))
            {
                Stopwatch firstChunkStopwatch = Stopwatch.StartNew();
                bool firstChunkEmitted = false;

                await foreach (AgentResponseUpdate chunk in aiAgent.RunStreamingAsync(
                                   new ChatMessage(ChatRole.User, req.Content!) { CreatedAt = DateTimeOffset.UtcNow }, aiAgentSession))
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
                        }
                    }
                }
            }
            else
            {
                AgentResponse response = await aiAgent.RunAsync(
                    new ChatMessage(ChatRole.User, req.Content!) { CreatedAt = DateTimeOffset.UtcNow }, aiAgentSession);
                fullResponse.Append(response.Text);
            }

            string llmResponseText = fullResponse.ToString();

            bool hasInteractiveToken = llmResponseText.Contains("#INT#", StringComparison.OrdinalIgnoreCase);
            bool endsWithQuestion = llmResponseText.EndsWith('?');

            #region LLM tools
            List<Records.QuickReply>? quickReplies = GetQuickRepliesFromContext(aiAgentSession);
            bool hasQuickReplies = quickReplies?.Count > 0;

            Records.RichCard? richCard = GetRichCardFromContext(aiAgentSession);
            bool hasRichCard = richCard != null;

            if (hasQuickReplies)
            {
                aiContextProvider.DropVariable(aiAgentSession, "quick_replies");
                agentLogger.LogInformation($"Dropped {quickReplies!.Count} quick replies from context (ephemeral data)");
            }

            if (hasRichCard)
            {
                aiContextProvider.DropVariable(aiAgentSession, "rich_card");
                agentLogger.LogInformation($"Dropped rich card '{richCard!.Title}' from context (ephemeral data)");
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
            agentSpan?.SetTag(MorganaTelemetry.AgentResponsePreview,
                responsePreview.Replace("#INT#", "", StringComparison.OrdinalIgnoreCase).Trim());
            agentSpan?.Dispose();

            await persistenceService.SaveAgentConversationAsync(AgentIdentifier, aiAgent, aiAgentSession, isCompleted);
            agentLogger.LogInformation($"Saved conversation state for {AgentIdentifier}");

#if DEBUG
            senderRef.Tell(new Records.AgentResponse(llmResponseText, isCompleted, quickReplies, richCard));
#else
            senderRef.Tell(new Records.AgentResponse(llmResponseText.Replace("#INT#", "", StringComparison.OrdinalIgnoreCase).Trim(), isCompleted, quickReplies, richCard));
#endif
        }
        catch (Exception ex)
        {
            agentLogger.LogError(ex, $"Error in {GetType().Name}");
            agentSpan?.SetStatus(ActivityStatusCode.Error, ex.Message);
            agentSpan?.Dispose();

            Self.Tell(new Records.FailureContext(new Status.Failure(ex), senderRef));
        }
    }

    private async Task HandleAgentFailureAsync(Records.FailureContext failure)
    {
        agentLogger.LogError(failure.Failure.Cause, $"Agent execution failed in {GetType().Name}");

        Records.Prompt morganaPrompt = await promptResolverService.ResolveAsync("Morgana");
        List<Records.ErrorAnswer> errorAnswers = morganaPrompt.GetAdditionalProperty<List<Records.ErrorAnswer>>("ErrorAnswers");
        Records.ErrorAnswer? genericError = errorAnswers.FirstOrDefault(e => string.Equals(e.Name, "GenericError", StringComparison.OrdinalIgnoreCase));

        failure.OriginalSender.Tell(new Records.AgentResponse(genericError?.Content ?? "An internal error occurred.", true, null));
    }

    protected List<Records.QuickReply>? GetQuickRepliesFromContext(AgentSession session)
    {
        #region Utilities
        List<Records.QuickReply>? GetQuickReplies(string quickRepliesJSON)
        {
            try
            {
                List<Records.QuickReply>? quickReplies = JsonSerializer.Deserialize<List<Records.QuickReply>>(quickRepliesJSON);
                if (quickReplies != null && quickReplies.Any())
                {
                    agentLogger.LogInformation($"Retrieved {quickReplies.Count} quick replies from context");
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

        if (ctxQuickReplies is string ctxQuickRepliesJson && !string.IsNullOrEmpty(ctxQuickRepliesJson))
            return GetQuickReplies(ctxQuickRepliesJson);

        if (ctxQuickReplies is JsonElement { ValueKind: JsonValueKind.String } ctxQuickRepliesJsonElement)
            return GetQuickReplies(ctxQuickRepliesJsonElement.GetString()!);

        return null;
    }

    protected Records.RichCard? GetRichCardFromContext(AgentSession session)
    {
        #region Utilities
        Records.RichCard? GetRichCard(string richCardJSON)
        {
            try
            {
                Records.RichCard? richCard = JsonSerializer.Deserialize<Records.RichCard>(
                    richCardJSON, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (richCard != null)
                {
                    agentLogger.LogInformation($"Retrieved rich card from context");
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

        if (ctxRichCard is string ctxRichCardJson && !string.IsNullOrEmpty(ctxRichCardJson))
            return GetRichCard(ctxRichCardJson);

        if (ctxRichCard is JsonElement { ValueKind: JsonValueKind.String } ctxRichCardJsonElement)
            return GetRichCard(ctxRichCardJsonElement.GetString()!);

        return null;
    }
}
