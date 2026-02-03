using Akka.Actor;
using Akka.Event;
using Morgana.Framework.Abstractions;
using Morgana.Framework.Extensions;
using Morgana.Framework.Interfaces;
using System.Text.Json;

namespace Morgana.Framework.Actors;

/// <summary>
/// <para>
/// Main orchestration actor that supervises the conversation flow through a finite state machine.
/// Coordinates guard checks, intent classification, agent routing and follow-up handling.
/// Manages presentation generation and tracks active agent state for multi-turn conversations.
/// </para>
/// <para>
/// Every UserMessage (new request or follow-up) passes through GuardActor first.
/// Guard check failure keeps FSM in current state, allowing user to retry without state change.
/// </para>
/// </summary>
/// <remarks>
/// <para><strong>State Machine:</strong></para>
/// <list type="bullet">
/// <item><term>Idle</term><description>Waiting for user messages. All messages are routed to guard check first.</description></item>
/// <item><term>AwaitingGuardCheck</term><description>Waiting for content moderation result from GuardActor. Can transition to Classification or FollowUp based on activeAgent state.</description></item>
/// <item><term>AwaitingClassification</term><description>Waiting for intent classification result from ClassifierActor (only for new requests).</description></item>
/// <item><term>AwaitingAgentResponse</term><description>Waiting for specialized agent to process the request.</description></item>
/// <item><term>AwaitingFollowUpResponse</term><description>Waiting for active agent to process follow-up message.</description></item>
/// </list>
/// <para><strong>Active Agent Tracking:</strong></para>
/// <para>When an agent signals incomplete processing (IsCompleted = false), the supervisor remembers the active agent
/// and routes subsequent messages directly to it (after guard check) until the agent signals completion.</para>
/// </remarks>
public class ConversationSupervisorActor : MorganaActor
{
    private readonly ISignalRBridgeService signalRBridgeService;
    private readonly IAgentConfigurationService agentConfigService;

    /* Actors directly orchestrated by the supervisor */
    private readonly IActorRef guard;
    private readonly IActorRef classifier;
    private readonly IActorRef router;

    /// <summary>
    /// Reference to the currently active agent (for multi-turn conversations).
    /// Null when no agent is active.
    /// </summary>
    private IActorRef? activeAgent;

    /// <summary>
    /// Intent name of the currently active agent.
    /// Used for agent name display and tracking.
    /// </summary>
    private string? activeAgentIntent;

    /// <summary>
    /// Flag indicating whether the presentation message has been sent.
    /// Prevents duplicate presentation on subsequent messages.
    /// </summary>
    private bool hasPresented;

    /// <summary>
    /// Initializes a new instance of the ConversationSupervisorActor.
    /// Creates child actors (guard, classifier, router) and enters Idle state.
    /// </summary>
    /// <param name="conversationId">Unique identifier for this conversation</param>
    /// <param name="llmService">LLM service for AI completions</param>
    /// <param name="promptResolverService">Service for resolving prompt templates</param>
    /// <param name="signalRBridgeService">Service for sending messages to clients via SignalR</param>
    /// <param name="agentConfigService">Service for loading intent and agent configurations</param>
    public ConversationSupervisorActor(
        string conversationId,
        ILLMService llmService,
        IPromptResolverService promptResolverService,
        ISignalRBridgeService signalRBridgeService,
        IAgentConfigurationService agentConfigService) : base(conversationId, llmService, promptResolverService)
    {
        this.signalRBridgeService = signalRBridgeService;
        this.agentConfigService = agentConfigService;

        guard = Context.System.GetOrCreateActor<GuardActor>(
            "guard", conversationId).GetAwaiter().GetResult();

        classifier = Context.System.GetOrCreateActor<ClassifierActor>(
            "classifier", conversationId).GetAwaiter().GetResult();

        router = Context.System.GetOrCreateActor<RouterActor>(
            "router", conversationId).GetAwaiter().GetResult();

        Idle();
    }

    #region State Behaviors

    /// <summary>
    /// Idle state: waiting for user messages or presentation requests.
    /// ALL user messages route through guard check first (whether new request or follow-up).
    /// </summary>
    private void Idle()
    {
        actorLogger.Info("↗ State: Idle");

        // Clear any lingering timeout from previous states
        Context.SetReceiveTimeout(null);

        // Generate and send welcome message with quick reply buttons on conversation start
        ReceiveAsync<Records.GeneratePresentationMessage>(HandlePresentationRequestAsync);
        ReceiveAsync<Records.PresentationContext>(HandlePresentationGenerated);

        // Handle incoming user messages - route through guard check using Tell pattern
        Receive<Records.UserMessage>(msg =>
        {
            IActorRef originalSender = Sender;

            actorLogger.Info("User message received, routing through guard check");

            Records.ProcessingContext ctx = new Records.ProcessingContext(msg, originalSender);

            // Become FIRST, then Tell
            Become(() => AwaitingGuardCheck(ctx));

            guard.Tell(new Records.GuardCheckRequest(msg.ConversationId, msg.Text));
        });

        // Re-register common handlers for FSM behavior consistency
        RegisterCommonHandlers();
    }

    /// <summary>
    /// Handles presentation generation requests.
    /// Generates a welcome message with quick replies using LLM or falls back to static template.
    /// </summary>
    /// <param name="_">Presentation request message (unused)</param>
    /// <remarks>
    /// <para>Presentation flow:</para>
    /// <list type="number">
    /// <item>Load presentation prompt from framework configuration</item>
    /// <item>Load available intents from domain configuration</item>
    /// <item>Call LLM to generate dynamic welcome message with quick replies</item>
    /// <item>On LLM failure, use fallback static message</item>
    /// <item>Send message to client via SignalR</item>
    /// </list>
    /// <para>Skips if presentation was already shown.</para>
    /// </remarks>
    private async Task HandlePresentationRequestAsync(Records.GeneratePresentationMessage _)
    {
        if (hasPresented)
        {
            actorLogger.Info("Presentation already shown, skipping");
            return;
        }

        hasPresented = true;
        actorLogger.Info("Generating LLM-driven presentation message");

        try
        {
            // Load presentation prompt from morgana.json (framework)
            Records.Prompt presentationPrompt = await promptResolverService.ResolveAsync("Presentation");

            // Load intents from domain
            List<Records.IntentDefinition> allIntents = await agentConfigService.GetIntentsAsync();

            Records.IntentCollection intentCollection = new Records.IntentCollection(allIntents);
            List<Records.IntentDefinition> displayableIntents = intentCollection.GetDisplayableIntents();

            if (displayableIntents.Count == 0)
            {
                actorLogger.Warning("No displayable intents available, sending presentation without quick replies");

                await Task.Delay(750); // Give SignalR time to join conversation

                await signalRBridgeService.SendStructuredMessageAsync(
                    conversationId,
                    presentationPrompt.GetAdditionalProperty<string>("NoAgentsMessage"),
                    "presentation",
                    [], // No quick replies
                    null,
                    "Morgana",
                    false);

                return;
            }

            // Format intents for LLM
            string formattedIntents = string.Join("\n",
                displayableIntents.Select(i => $"- {i.Name}: {i.Description}"));

            // Build LLM prompt
            string systemPrompt = $"{presentationPrompt.Target}\n\n{presentationPrompt.Instructions}"
                .Replace("((intents))", formattedIntents);

            actorLogger.Info("Invoking LLM to generate presentation message");

            string llmResponse = await llmService.CompleteWithSystemPromptAsync(
                conversationId,
                systemPrompt,
                "Generate the presentation");

            actorLogger.Info("LLM presentation generated successfully");

            Records.PresentationResponse? presentation =
                JsonSerializer.Deserialize<Records.PresentationResponse>(llmResponse);

            if (presentation != null)
            {
                actorLogger.Info($"LLM generated {presentation.QuickReplies.Count} quick replies");

                Self.Tell(new Records.PresentationContext(presentation.Message, displayableIntents)
                {
                    LLMQuickReplies = presentation.QuickReplies
                });
            }
            else
            {
                throw new InvalidOperationException("LLM returned null presentation");
            }
        }
        catch (Exception ex)
        {
            actorLogger.Error(ex, "LLM presentation generation failed, using fallback");

            // Fallback: use static message
            Records.Prompt fallbackPrompt = await promptResolverService.ResolveAsync("Presentation");
            string fallbackMessage = fallbackPrompt.GetAdditionalProperty<string>("FallbackMessage");

            List<Records.IntentDefinition> allIntents = await agentConfigService.GetIntentsAsync();

            Records.IntentCollection intentCollection = new Records.IntentCollection(allIntents);
            List<Records.IntentDefinition> displayableIntents = intentCollection.GetDisplayableIntents();

            List<Records.QuickReply> fallbackReplies = displayableIntents
                .ConvertAll(intent => new Records.QuickReply(
                    intent.Name,
                    intent.Label ?? intent.Name,
                    intent.DefaultValue ?? $"Help me with {intent.Name}"));

            actorLogger.Info($"Using fallback presentation with {fallbackReplies.Count} quick replies");

            Self.Tell(new Records.PresentationContext(fallbackMessage, displayableIntents)
            {
                LLMQuickReplies = fallbackReplies
            });
        }
    }

    /// <summary>
    /// Handles the generated presentation and sends it to the client via SignalR.
    /// </summary>
    /// <param name="ctx">Context containing the presentation message and quick replies</param>
    private async Task HandlePresentationGenerated(Records.PresentationContext ctx)
    {
        actorLogger.Info("Sending presentation to client via SignalR");

        // Convert LLM-generated quick replies to SignalR format
        List<Records.QuickReply> quickReplies = ctx.LLMQuickReplies?
            .Select(qr => new Records.QuickReply(qr.Id, qr.Label, qr.Value))
            .ToList() ?? [];

        try
        {
            await Task.Delay(750); // Give SignalR time to join conversation

            await signalRBridgeService.SendStructuredMessageAsync(
                conversationId,
                ctx.Message,
                "presentation",
                quickReplies,
                null,
                "Morgana",
                false);

            actorLogger.Info("Presentation sent successfully");
        }
        catch (Exception ex)
        {
            actorLogger.Error(ex, "Failed to send presentation via SignalR");
        }
    }

    /// <summary>
    /// <para>
    /// AwaitingGuardCheck state: waiting for content moderation result from GuardActor.
    /// This state is reached from BOTH new requests AND follow-ups.
    /// </para>
    /// <para>
    /// On pass: transitions to next appropriate state (Classification for new, FollowUp for active agent)
    /// On fail: sends violation message to client and returns to Idle (user can retry)
    /// </para>
    /// </summary>
    /// <param name="ctx">Processing context containing original message and sender</param>
    private void AwaitingGuardCheck(Records.ProcessingContext ctx)
    {
        actorLogger.Info("→ State: AwaitingGuardCheck");

        // Handle content moderation result from GuardActor:
        // - If compliant AND activeAgent exists: route to active agent (follow-up flow)
        // - If compliant AND no activeAgent: route to classifier (new request flow)
        // - If violation: send rejection message and return to Idle (allow user retry)
        ReceiveAsync<Records.GuardCheckResponse>(async response =>
        {
            if (!response.Compliant)
            {
                actorLogger.Warning($"Message rejected by guard: {response.Violation}");

                Records.Prompt guardPrompt = await promptResolverService.ResolveAsync("Guard");
                string guardAnswer = guardPrompt.GetAdditionalProperty<string>("GuardAnswer")
                    .Replace("((violation))", response.Violation ?? "Content policy violation");

                string currentAgentName = activeAgentIntent != null ? GetAgentDisplayName(activeAgentIntent) : "Morgana";
                ctx.OriginalSender.Tell(new Records.ConversationResponse(
                    guardAnswer, ctx.Classification?.Intent, null, currentAgentName, false));

                // Return to Idle - user can retry the message
                Become(Idle);
                return;
            }

            actorLogger.Info("Message passed guard check");

            if (activeAgent != null)
            {
                actorLogger.Info($"Active agent exists, routing to follow-up flow with agent {activeAgent.Path}");

                // Become BEFORE Tell so we're ready to receive streaming chunks
                Become(() => AwaitingFollowUpResponse(ctx.OriginalSender));

                // Route directly to active agent (follow-up) using Tell
                activeAgent.Tell(new Records.AgentRequest(
                    ctx.OriginalMessage.ConversationId,
                    ctx.OriginalMessage.Text,
                    null));
            }
            else
            {
                actorLogger.Info("No active agent, proceeding to classification for new request");

                // Become FIRST, then Tell classifier
                Become(() => AwaitingClassification(ctx));

                classifier.Tell(ctx.OriginalMessage);
            }
        });

        Receive<Status.Failure>(failure =>
        {
            actorLogger.Error(failure.Cause, "Guard check failed");

            // Fail-open: allow message through on guard error
            actorLogger.Warning("Guard check failed, failing open (allowing message)");

            if (activeAgent != null)
            {
                // Become FIRST, then Tell active agent
                Become(() => AwaitingFollowUpResponse(ctx.OriginalSender));

                activeAgent.Tell(new Records.AgentRequest(
                    ctx.OriginalMessage.ConversationId,
                    ctx.OriginalMessage.Text,
                    null));
            }
            else
            {
                // Become FIRST, then Tell classifier
                Become(() => AwaitingClassification(ctx));

                classifier.Tell(ctx.OriginalMessage);
            }
        });

        // Re-register common handlers for FSM behavior consistency
        RegisterCommonHandlers();
    }

    /// <summary>
    /// AwaitingClassification state: waiting for intent classification result from ClassifierActor.
    /// Only reached after guard check passes for NEW requests (no active agent).
    /// On success: forwards to RouterActor and transitions to AwaitingAgentResponse.
    /// On failure: falls back to "other" intent to maintain conversation flow.
    /// </summary>
    /// <param name="ctx">Processing context containing original message and sender</param>
    private void AwaitingClassification(Records.ProcessingContext ctx)
    {
        actorLogger.Info("→ State: AwaitingClassification");

        // Handle intent classification result from ClassifierActor:
        // - Classification successful: forward to RouterActor to find appropriate agent → AwaitingAgentResponse
        // - Updates processing context with classification metadata for agent routing
        Receive<Records.ClassificationResult>(classification =>
        {
            actorLogger.Info($"Classification result: {classification.Intent}");

            Records.ProcessingContext updatedCtx = ctx with { Classification = classification };

            // Become BEFORE Tell so we're ready to receive streaming chunks
            Become(() => AwaitingAgentResponse(updatedCtx));

            // Route to router using Tell (not Ask) to support streaming
            router.Tell(new Records.AgentRequest(
                ctx.OriginalMessage.ConversationId,
                ctx.OriginalMessage.Text,
                classification));
        });

        Receive<Status.Failure>(failure =>
        {
            actorLogger.Error(failure.Cause, "Classification failed");

            // Fallback to "other" intent to maintain conversation flow
            Records.ClassificationResult fallbackClassification = new Records.ClassificationResult(
                "other",
                new Dictionary<string, string>
                {
                    ["confidence"] = "0.00",
                    ["error"] = $"classification_failed: {failure.Cause.Message}"
                });

            actorLogger.Info("Falling back to 'other' intent");
            Records.ProcessingContext updatedCtx = ctx with { Classification = fallbackClassification };

            // Become FIRST, then Tell router
            Become(() => AwaitingAgentResponse(updatedCtx));

            router.Tell(new Records.AgentRequest(
                ctx.OriginalMessage.ConversationId,
                ctx.OriginalMessage.Text,
                fallbackClassification));
        });

        // Re-register common handlers for FSM behavior consistency
        RegisterCommonHandlers();
    }

    /// <summary>
    /// AwaitingAgentResponse state: waiting for specialized agent to process the request.
    /// Only reached after classification for NEW requests.
    /// Handles both ActiveAgentResponse (from specialized agents) and AgentResponse (from router fallback).
    /// Updates active agent tracking based on agent completion status.
    /// </summary>
    /// <param name="ctx">Processing context containing original message, sender, and classification</param>
    /// <remarks>
    /// If agent signals incomplete (IsCompleted = false), the agent becomes "active" and subsequent messages
    /// are routed directly to it (after guard check) until it signals completion.
    /// </remarks>
    private void AwaitingAgentResponse(Records.ProcessingContext ctx)
    {
        actorLogger.Info("→ State: AwaitingAgentResponse");

        // Set timeout for agent response (90 seconds)
        Context.SetReceiveTimeout(TimeSpan.FromSeconds(90));

        // Handle timeout if agent doesn't respond
        Receive<ReceiveTimeout>(_ =>
        {
            actorLogger.Error($"Timeout waiting for agent response (classification: {ctx.Classification?.Intent})");

            Context.SetReceiveTimeout(null); // Clear timeout

            ctx.OriginalSender.Tell(new Records.ConversationResponse(
                "I apologize, but the request took too long to process. Please try again.",
                ctx.Classification?.Intent,
                ctx.Classification?.Metadata,
                GetAgentDisplayName(ctx.Classification?.Intent),
                false,
                null,
                ctx.OriginalMessage.Timestamp));

            Become(Idle);
        });

        // Forward streaming chunks from agent to manager (and then to client)
        // Reset timeout on each chunk to prevent timeout during active streaming
        Receive<Records.AgentStreamChunk>(chunk =>
        {
            // Reset timeout - we're getting data, agent is alive
            Context.SetReceiveTimeout(TimeSpan.FromSeconds(90));
            ctx.OriginalSender.Tell(chunk);
        });

        // Handle specialized agent response with active agent tracking (direct Tell from router)
        Receive<Records.ActiveAgentResponse>(response =>
        {
            // Clear timeout immediately upon receiving response
            Context.SetReceiveTimeout(null);

            try
            {
                string agentName = GetAgentDisplayName(ctx.Classification?.Intent);
                int quickRepliesCount = response.QuickReplies?.Count ?? 0;
                
                actorLogger.Info($"Received ActiveAgentResponse from {response.AgentRef.Path}, completed: {response.IsCompleted}, quickReplies: {quickRepliesCount}");

                if (response.IsCompleted)
                {
                    actorLogger.Info("Agent signaled completion, clearing active agent");

                    activeAgent = null;
                    activeAgentIntent = null;
                }
                else
                {
                    actorLogger.Info($"Agent signaled incomplete, setting as active agent: {response.AgentRef.Path}");

                    activeAgent = response.AgentRef;
                    activeAgentIntent = ctx.Classification?.Intent; // Store intent from classification
                }

                ctx.OriginalSender.Tell(new Records.ConversationResponse(
                    response.Response,
                    ctx.Classification?.Intent,
                    ctx.Classification?.Metadata,
                    agentName,
                    response.IsCompleted,
                    response.QuickReplies,
                    ctx.OriginalMessage.Timestamp));

                Become(Idle);
            }
            catch (Exception ex)
            {
                actorLogger.Error(ex, "Error processing ActiveAgentResponse");

                // Clear active agent on error
                activeAgent = null;
                activeAgentIntent = null;

                // Send error response to manager
                ctx.OriginalSender.Tell(new Records.ConversationResponse(
                    "An error occurred while processing the response. Please try again.",
                    ctx.Classification?.Intent,
                    ctx.Classification?.Metadata,
                    GetAgentDisplayName(ctx.Classification?.Intent),
                    false,
                    null,
                    ctx.OriginalMessage.Timestamp));

                Become(Idle);
            }
        });

        // Handle fallback response from router (no specialized agent available)
        Receive<Records.AgentResponse>(response =>
        {
            // Clear timeout immediately upon receiving response
            Context.SetReceiveTimeout(null);

            try
            {
                actorLogger.Info("Received fallback response from router (no specialized agent)");

                ctx.OriginalSender.Tell(new Records.ConversationResponse(
                    response.Response,
                    ctx.Classification?.Intent,
                    null,
                    "Morgana",
                    true,
                    response.QuickReplies,
                    DateTime.UtcNow));

                Become(Idle);
            }
            catch (Exception ex)
            {
                actorLogger.Error(ex, "Error processing fallback AgentResponse");

                ctx.OriginalSender.Tell(new Records.ConversationResponse(
                    "An error occurred while processing the response. Please try again.",
                    ctx.Classification?.Intent,
                    null,
                    "Morgana",
                    false,
                    null,
                    DateTime.UtcNow));

                Become(Idle);
            }
        });

        // Re-register common handlers for FSM behavior consistency
        RegisterCommonHandlers();
    }

    /// <summary>
    /// AwaitingFollowUpResponse state: waiting for active agent to process follow-up message.
    /// Only reached when an active agent exists (multi-turn conversation).
    /// Routes messages directly to the active agent, bypassing classification.
    /// </summary>
    /// <param name="originalSender">Original sender reference for response routing</param>
    private void AwaitingFollowUpResponse(IActorRef originalSender)
    {
        actorLogger.Info("→ State: AwaitingFollowUpResponse");

        // Set timeout for follow-up agent response (90 seconds)
        Context.SetReceiveTimeout(TimeSpan.FromSeconds(90));

        // Handle timeout if agent doesn't respond
        Receive<ReceiveTimeout>(_ =>
        {
            actorLogger.Error($"Timeout waiting for follow-up response from active agent (intent: {activeAgentIntent})");

            Context.SetReceiveTimeout(null); // Clear timeout

            // Clear active agent on timeout
            activeAgent = null;
            activeAgentIntent = null;

            originalSender.Tell(new Records.ConversationResponse(
                "I apologize, but the request took too long to process. Please try again.",
                null,
                null,
                "Morgana",
                false,
                null,
                DateTime.UtcNow));

            Become(Idle);
        });

        // Forward streaming chunks from active agent to manager (and then to client)
        // Reset timeout on each chunk to prevent timeout during active streaming
        Receive<Records.AgentStreamChunk>(chunk =>
        {
            // Reset timeout - we're getting data, agent is alive
            Context.SetReceiveTimeout(TimeSpan.FromSeconds(90));
            originalSender.Tell(chunk);
        });

        // Handle follow-up response from currently active agent (direct Tell, not PipeTo)
        Receive<Records.AgentResponse>(response =>
        {
            // Clear timeout immediately upon receiving response
            Context.SetReceiveTimeout(null);

            try
            {
                string agentName = activeAgentIntent != null ? GetAgentDisplayName(activeAgentIntent) : "Morgana";

                if (response.IsCompleted)
                {
                    actorLogger.Info("Active agent signaled completion, clearing active agent");

                    activeAgent = null;
                    activeAgentIntent = null;
                }

                originalSender.Tell(new Records.ConversationResponse(
                    response.Response,
                    null,
                    null,
                    agentName,
                    response.IsCompleted,
                    response.QuickReplies,
                    DateTime.UtcNow)); // Using UtcNow since we don't have original timestamp with Tell

                Become(Idle);
            }
            catch (Exception ex)
            {
                actorLogger.Error(ex, "Error processing follow-up AgentResponse");

                // Clear active agent on error
                activeAgent = null;
                activeAgentIntent = null;

                // Send error response to manager
                originalSender.Tell(new Records.ConversationResponse(
                    "An error occurred while processing the response. Please try again.",
                    null,
                    null,
                    "Morgana",
                    false,
                    null,
                    DateTime.UtcNow));

                Become(Idle);
            }
        });

        // Re-register common handlers for FSM behavior consistency
        RegisterCommonHandlers();
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Gets the display name for an agent based on its intent.
    /// Returns "Morgana" for the "other" intent or missing intents.
    /// Returns "Morgana (Intent)" for specialized intents.
    /// </summary>
    /// <param name="intent">Intent name to format</param>
    /// <returns>Formatted agent display name</returns>
    private string GetAgentDisplayName(string? intent)
    {
        if (string.IsNullOrEmpty(intent) || string.Equals(intent, "other", StringComparison.OrdinalIgnoreCase))
            return "Morgana";

        // Capitalize first letter of intent for display
        string capitalizedIntent = char.ToUpper(intent[0]) + intent[1..];

        return $"Morgana ({capitalizedIntent})";
    }

    /// <summary>
    /// Handles unexpected failures in the Idle state.
    /// Sends error message to sender and remains in Idle state.
    /// </summary>
    /// <param name="failure">Failure information</param>
    private void HandleUnexpectedFailure(Records.FailureContext failure)
    {
        actorLogger.Error(failure.Failure.Cause, "Unexpected failure in ConversationSupervisorActor");

        failure.OriginalSender.Tell(
            new Records.ConversationResponse("An internal error occurred.", null, null, "Morgana", false));
    }

    /// <summary>
    /// Restores the active agent state when resuming a conversation from persistence.
    /// This allows multi-turn conversations to continue seamlessly after application restart.
    /// Registered as common handler across all FSM states.
    /// </summary>
    /// <param name="msg">Restoration request containing the agent intent</param>
    private void HandleRestoreActiveAgent(Records.RestoreActiveAgent msg)
    {
        actorLogger.Info($"Restoring active agent: {msg.AgentIntent}");

        // No active agent -> Fallback to Morgana
        if (string.Equals(msg.AgentIntent, "Morgana", StringComparison.OrdinalIgnoreCase))
        {
            // Fallback: clear active agent, next message will reclassify
            activeAgent = null;
            activeAgentIntent = null;

            actorLogger.Info($"No active agent detected: fallback to Morgana");
            return;
        }

        // Delegate to router for agent resolution and caching using Tell pattern
        router.Tell(new Records.RestoreAgentRequest(msg.AgentIntent));
    }

    /// <summary>
    /// Handles the response from RouterActor after agent restoration request.
    /// Sets or clears the active agent based on resolution success.
    /// </summary>
    /// <param name="response">Response containing resolved agent reference or null</param>
    private void HandleRestoreAgentResponse(Records.RestoreAgentResponse response)
    {
        if (response.AgentRef != null)
        {
            activeAgent = response.AgentRef;
            activeAgentIntent = response.AgentIntent;

            actorLogger.Info($"Active agent restored: {activeAgent.Path} with intent {activeAgentIntent}");
        }
        else
        {
            // Intent not recognized or agent not available
            // Fallback: clear active agent, next message will reclassify
            activeAgent = null;
            activeAgentIntent = null;

            actorLogger.Warning($"Could not restore agent for intent '{response.AgentIntent}' - clearing active agent");
        }
    }

    /// <inheritdoc/>
    protected override void RegisterCommonHandlers()
    {
        base.RegisterCommonHandlers();

        // Specific handlers for supervisor
        Receive<Records.RestoreActiveAgent>(HandleRestoreActiveAgent);
        Receive<Records.RestoreAgentResponse>(HandleRestoreAgentResponse);
        Receive<Records.FailureContext>(HandleUnexpectedFailure);
    }

    #endregion
}