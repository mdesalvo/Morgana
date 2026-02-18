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
/// Extends MorganaActor with AI agent capabilities, conversation session, context management and inter-agent communication.
/// </summary>
/// <remarks>
/// <para><strong>OpenTelemetry:</strong></para>
/// <para>ExecuteAgentAsync opens a morgana.agent Activity as child of AgentRequest.TurnContext.
/// It emits a "first_chunk" event on TTFT (time-to-first-token) and sets agent.ttft_ms,
/// agent.response_preview, agent.is_completed, and agent.has_quick_replies at close time.</para>
/// </remarks>
public class MorganaAgent : MorganaActor
{
    protected AIAgent aiAgent;
    protected AgentSession? aiAgentSession;
    protected MorganaAIContextProvider aiContextProvider;
    protected readonly IConversationPersistenceService persistenceService;
    protected readonly ILogger agentLogger;

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
    /// Deserializes a previously serialized AgentSession, restoring conversation history and context state.
    /// </summary>
    public virtual async Task<AgentSession> DeserializeSessionAsync(
        JsonElement serializedState,
        JsonSerializerOptions? jsonSerializerOptions = null)
    {
        jsonSerializerOptions ??= AgentAbstractionsJsonUtilities.DefaultOptions;

        JsonElement aiContextProviderState = default;
        if (serializedState.TryGetProperty("aiContextProviderState", out JsonElement stateElement))
            aiContextProviderState = stateElement;

        if (aiContextProviderState.ValueKind != JsonValueKind.Undefined)
            aiContextProvider.RestoreState(aiContextProviderState, jsonSerializerOptions);

        aiContextProvider.OnSharedContextUpdate = OnSharedContextUpdate;
        aiContextProvider.PropagateSharedVariables();

        aiAgentSession = await aiAgent.DeserializeSessionAsync(serializedState, jsonSerializerOptions);

        agentLogger.LogInformation($"Deserialized AgentSession for conversation {conversationId}");

        return aiAgentSession;
    }

    protected void OnSharedContextUpdate(string key, object value)
    {
        agentLogger.LogInformation($"Agent {AgentIntent} broadcasting shared context variable: {key}");

        Context.System.GetOrCreateActorAsync<RouterActor>("router", conversationId)
            .GetAwaiter()
            .GetResult()
            .Tell(new Records.BroadcastContextUpdate(AgentIntent, new Dictionary<string, object> { [key] = value }));
    }

    private void HandleContextUpdate(Records.ReceiveContextUpdate msg)
    {
        agentLogger.LogInformation(
            $"Agent '{AgentIntent}' received shared context from '{msg.SourceAgentIntent}': {string.Join(", ", msg.UpdatedValues.Keys)}");

        aiContextProvider.MergeSharedContext(msg.UpdatedValues);
    }

    /// <summary>
    /// Executes the agent using AgentSession for automatic conversation history and context management.
    /// Opens a morgana.agent OTel span as child of AgentRequest.TurnContext. Records TTFT on first chunk.
    /// </summary>
    /// <param name="req">Agent request containing the user's message, optional classification, and OTel TurnContext</param>
    protected async Task ExecuteAgentAsync(Records.AgentRequest req)
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

                agentSpan?.AddEvent(new ActivityEvent(MorganaTelemetry.CreateAgentConversation));
                agentSpan?.SetTag(MorganaTelemetry.AgentIdentifier, AgentIdentifier);
            }
            else
            {
                aiAgentSession = await aiAgent.CreateSessionAsync();
                agentLogger.LogInformation($"Created new conversation session for {AgentIdentifier}");

                agentSpan?.AddEvent(new ActivityEvent(MorganaTelemetry.ResumeAgentConversation));
                agentSpan?.SetTag(MorganaTelemetry.AgentIdentifier, AgentIdentifier);
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

            List<Records.QuickReply>? quickReplies = GetQuickRepliesFromContext();
            bool hasQuickReplies = quickReplies?.Count > 0;

            Records.RichCard? richCard = GetRichCardFromContext();
            bool hasRichCard = richCard != null;

            if (hasQuickReplies)
            {
                aiContextProvider.DropVariable("quick_replies");
                agentLogger.LogInformation($"Dropped {quickReplies!.Count} quick replies from context (ephemeral data)");
            }

            if (hasRichCard)
            {
                aiContextProvider.DropVariable("rich_card");
                agentLogger.LogInformation($"Dropped rich card '{richCard!.Title}' from context (ephemeral data)");
            }

            bool isCompleted = !hasInteractiveToken && !endsWithQuestion && !hasQuickReplies && !hasRichCard;

            agentLogger.LogInformation(
                $"Agent response analysis: HasINT={hasInteractiveToken}," +
                $"EndsWithQuestion={endsWithQuestion}," +
                $"HasQuickReplies={hasQuickReplies}," +
                $"HasRichCard={hasRichCard}," +
                $"IsCompleted={isCompleted}");

            // Finalise agent span with outcome attributes
            string responsePreview = llmResponseText.Length > 150
                ? llmResponseText[..150]
                : llmResponseText;
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

    protected List<Records.QuickReply>? GetQuickRepliesFromContext()
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
                aiContextProvider.DropVariable("quick_replies");
            }

            return null;
        }
        #endregion

        object? ctxQuickReplies = aiContextProvider.GetVariable("quick_replies");

        if (ctxQuickReplies is string ctxQuickRepliesJson && !string.IsNullOrEmpty(ctxQuickRepliesJson))
            return GetQuickReplies(ctxQuickRepliesJson);

        if (ctxQuickReplies is JsonElement { ValueKind: JsonValueKind.String } ctxQuickRepliesJsonElement)
            return GetQuickReplies(ctxQuickRepliesJsonElement.GetString()!);

        return null;
    }

    protected Records.RichCard? GetRichCardFromContext()
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
                aiContextProvider.DropVariable("rich_card");
            }

            return null;
        }
        #endregion

        object? ctxRichCard = aiContextProvider.GetVariable("rich_card");

        if (ctxRichCard is string ctxRichCardJson && !string.IsNullOrEmpty(ctxRichCardJson))
            return GetRichCard(ctxRichCardJson);

        if (ctxRichCard is JsonElement { ValueKind: JsonValueKind.String } ctxRichCardJsonElement)
            return GetRichCard(ctxRichCardJsonElement.GetString()!);

        return null;
    }
}