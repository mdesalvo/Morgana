using System.Diagnostics;
using System.Text.Json;
using Akka.Actor;
using Akka.Event;
using Microsoft.Extensions.Configuration;
using Morgana.Framework.Abstractions;
using Morgana.Framework.Extensions;
using Morgana.Framework.Interfaces;
using Morgana.Framework.Telemetry;

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
/// <para><strong>OpenTelemetry:</strong></para>
/// <para>The supervisor opens child spans (morgana.guard, morgana.classifier) using the TurnContext carried
/// inside ProcessingContext. The TurnContext for the agent span is passed via AgentRequest.TurnContext
/// so that MorganaAgent can open morgana.agent as a child of the correct turn span.</para>
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
    public ConversationSupervisorActor(
        string conversationId,
        ILLMService llmService,
        IPromptResolverService promptResolverService,
        ISignalRBridgeService signalRBridgeService,
        IAgentConfigurationService agentConfigService,
        IConfiguration configuration) : base(conversationId, llmService, promptResolverService, configuration)
    {
        this.signalRBridgeService = signalRBridgeService;
        this.agentConfigService = agentConfigService;

        guard = Context.System.GetOrCreateActorAsync<GuardActor>(
            "guard", conversationId).GetAwaiter().GetResult();

        classifier = Context.System.GetOrCreateActorAsync<ClassifierActor>(
            "classifier", conversationId).GetAwaiter().GetResult();

        router = Context.System.GetOrCreateActorAsync<RouterActor>(
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

        Context.SetReceiveTimeout(null);

        ReceiveAsync<Records.GeneratePresentationMessage>(HandlePresentationRequestAsync);
        ReceiveAsync<Records.PresentationContext>(HandlePresentationGenerated);

        Receive<Records.UserMessage>(msg =>
        {
            IActorRef originalSender = Sender;

            actorLogger.Info("User message received, routing through guard check");

            // Lift TurnContext from UserMessage into ProcessingContext so it flows
            // through all FSM states without further changes to individual state handlers.
            Records.ProcessingContext ctx = new Records.ProcessingContext(
                msg, originalSender, TurnContext: msg.TurnContext);

            Become(() => AwaitingGuardCheck(ctx));

            guard.Tell(new Records.GuardCheckRequest(msg.ConversationId, msg.Text));
        });

        RegisterCommonHandlers();
    }

    /// <summary>
    /// Handles presentation generation requests.
    /// </summary>
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
            Records.Prompt presentationPrompt = await promptResolverService.ResolveAsync("Presentation");
            List<Records.IntentDefinition> allIntents = await agentConfigService.GetIntentsAsync();
            Records.IntentCollection intentCollection = new Records.IntentCollection(allIntents);
            List<Records.IntentDefinition> displayableIntents = intentCollection.GetDisplayableIntents();

            if (displayableIntents.Count == 0)
            {
                actorLogger.Warning("No displayable intents available, sending presentation without quick replies");

                await Task.Delay(750);

                await signalRBridgeService.SendStructuredMessageAsync(
                    conversationId,
                    presentationPrompt.GetAdditionalProperty<string>("NoAgentsMessage"),
                    "presentation",
                    [],
                    null,
                    "Morgana",
                    false,
                    null);

                return;
            }

            string formattedIntents = string.Join("\n",
                displayableIntents.Select(i => $"- {i.Name}: {i.Description}"));

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
    private async Task HandlePresentationGenerated(Records.PresentationContext ctx)
    {
        actorLogger.Info("Sending presentation to client via SignalR");

        List<Records.QuickReply> quickReplies = ctx.LLMQuickReplies?
            .Select(qr => new Records.QuickReply(qr.Id, qr.Label, qr.Value))
            .ToList() ?? [];

        try
        {
            await Task.Delay(750);

            await signalRBridgeService.SendStructuredMessageAsync(
                conversationId,
                ctx.Message,
                "presentation",
                quickReplies,
                null,
                "Morgana",
                false,
                null);

            actorLogger.Info("Presentation sent successfully");
        }
        catch (Exception ex)
        {
            actorLogger.Error(ex, "Failed to send presentation via SignalR");
        }
    }

    /// <summary>
    /// AwaitingGuardCheck state: waiting for content moderation result from GuardActor.
    /// Opens a morgana.guard child span using the TurnContext from ProcessingContext.
    /// </summary>
    /// <param name="ctx">Processing context containing original message, sender, and OTel TurnContext</param>
    private void AwaitingGuardCheck(Records.ProcessingContext ctx)
    {
        actorLogger.Info("→ State: AwaitingGuardCheck");

        ReceiveAsync<Records.GuardCheckResponse>(async response =>
        {
            // Open guard span as child of the turn span
            using Activity? guardSpan = MorganaTelemetry.Source.StartActivity(
                MorganaTelemetry.GuardActivity,
                ActivityKind.Internal,
                ctx.TurnContext);
            guardSpan?.SetTag(MorganaTelemetry.ConversationId, conversationId);
            guardSpan?.SetTag(MorganaTelemetry.GuardCompliant, response.Compliant);
            if (!response.Compliant && response.Violation != null)
                guardSpan?.SetTag(MorganaTelemetry.GuardViolation, response.Violation);

            if (!response.Compliant)
            {
                actorLogger.Warning($"Message rejected by guard: {response.Violation}");

                Records.Prompt guardPrompt = await promptResolverService.ResolveAsync("Guard");
                string guardAnswer = guardPrompt.GetAdditionalProperty<string>("GuardAnswer")
                    .Replace("((violation))", response.Violation ?? "Content policy violation");

                string currentAgentName = activeAgentIntent != null ? GetAgentDisplayName(activeAgentIntent) : "Morgana";
                ctx.OriginalSender.Tell(new Records.ConversationResponse(
                    guardAnswer, ctx.Classification?.Intent, null, currentAgentName, false));

                Become(Idle);
                return;
            }

            actorLogger.Info("Message passed guard check");

            if (activeAgent != null)
            {
                actorLogger.Info($"Active agent exists, routing to follow-up flow with agent {activeAgent.Path}");

                Become(() => AwaitingFollowUpResponse(ctx.OriginalSender));

                activeAgent.Tell(new Records.AgentRequest(
                    ctx.OriginalMessage.ConversationId,
                    ctx.OriginalMessage.Text,
                    null,
                    ctx.TurnContext));        // propagate context to agent
            }
            else
            {
                actorLogger.Info("No active agent, proceeding to classification for new request");

                Become(() => AwaitingClassification(ctx));

                classifier.Tell(ctx.OriginalMessage);
            }
        });

        Receive<Status.Failure>(failure =>
        {
            actorLogger.Error(failure.Cause, "Guard check failed");
            actorLogger.Warning("Guard check failed, failing open (allowing message)");

            if (activeAgent != null)
            {
                Become(() => AwaitingFollowUpResponse(ctx.OriginalSender));

                activeAgent.Tell(new Records.AgentRequest(
                    ctx.OriginalMessage.ConversationId,
                    ctx.OriginalMessage.Text,
                    null,
                    ctx.TurnContext));
            }
            else
            {
                Become(() => AwaitingClassification(ctx));

                classifier.Tell(ctx.OriginalMessage);
            }
        });

        RegisterCommonHandlers();
    }

    /// <summary>
    /// AwaitingClassification state: waiting for intent classification result from ClassifierActor.
    /// Opens a morgana.classifier child span using the TurnContext from ProcessingContext.
    /// </summary>
    /// <param name="ctx">Processing context containing original message, sender, and OTel TurnContext</param>
    private void AwaitingClassification(Records.ProcessingContext ctx)
    {
        actorLogger.Info("→ State: AwaitingClassification");

        Receive<Records.ClassificationResult>(classification =>
        {
            actorLogger.Info($"Classification result: {classification.Intent}");

            // Open classifier span as child of the turn span
            using Activity? classifierSpan = MorganaTelemetry.Source.StartActivity(
                MorganaTelemetry.ClassifierActivity,
                ActivityKind.Internal,
                ctx.TurnContext);
            classifierSpan?.SetTag(MorganaTelemetry.ConversationId, conversationId);
            classifierSpan?.SetTag(MorganaTelemetry.ClassificationIntent, classification.Intent);
            if (classification.Metadata.TryGetValue("confidence", out string? confidence))
                classifierSpan?.SetTag(MorganaTelemetry.ClassificationConfidence, confidence);

            Records.ProcessingContext updatedCtx = ctx with { Classification = classification };

            Become(() => AwaitingAgentResponse(updatedCtx));

            router.Tell(new Records.AgentRequest(
                ctx.OriginalMessage.ConversationId,
                ctx.OriginalMessage.Text,
                classification,
                ctx.TurnContext));           // propagate context to router → agent
        });

        Receive<Status.Failure>(failure =>
        {
            actorLogger.Error(failure.Cause, "Classification failed");

            Records.ClassificationResult fallbackClassification = new Records.ClassificationResult(
                "other",
                new Dictionary<string, string>
                {
                    ["confidence"] = "0.00",
                    ["error"] = $"classification_failed: {failure.Cause.Message}"
                });

            actorLogger.Info("Falling back to 'other' intent");
            Records.ProcessingContext updatedCtx = ctx with { Classification = fallbackClassification };

            Become(() => AwaitingAgentResponse(updatedCtx));

            router.Tell(new Records.AgentRequest(
                ctx.OriginalMessage.ConversationId,
                ctx.OriginalMessage.Text,
                fallbackClassification,
                ctx.TurnContext));
        });

        RegisterCommonHandlers();
    }

    /// <summary>
    /// AwaitingAgentResponse state: waiting for specialized agent to process the request.
    /// Annotates the turn span with agent name and intent on response.
    /// </summary>
    /// <param name="ctx">Processing context containing original message, sender, and classification</param>
    private void AwaitingAgentResponse(Records.ProcessingContext ctx)
    {
        actorLogger.Info("→ State: AwaitingAgentResponse");

        Context.SetReceiveTimeout(TimeSpan.FromSeconds(90));

        Receive<ReceiveTimeout>(_ =>
        {
            actorLogger.Error($"Timeout waiting for agent response (classification: {ctx.Classification?.Intent})");

            Context.SetReceiveTimeout(null);

            ctx.OriginalSender.Tell(new Records.ConversationResponse(
                "I apologize, but the request took too long to process. Please try again.",
                ctx.Classification?.Intent,
                ctx.Classification?.Metadata,
                GetAgentDisplayName(ctx.Classification?.Intent),
                false,
                null,
                ctx.OriginalMessage.Timestamp,
                null));

            Become(Idle);
        });

        Receive<Records.AgentStreamChunk>(chunk =>
        {
            Context.SetReceiveTimeout(TimeSpan.FromSeconds(90));
            ctx.OriginalSender.Tell(chunk);
        });

        Receive<Records.ActiveAgentResponse>(response =>
        {
            Context.SetReceiveTimeout(null);

            try
            {
                string agentName = GetAgentDisplayName(ctx.Classification?.Intent);
                int quickRepliesCount = response.QuickReplies?.Count ?? 0;

                actorLogger.Info($"Received ActiveAgentResponse from {response.AgentRef.Path}, completed: {response.IsCompleted}, quickReplies: {quickRepliesCount}");

                // Annotate the turn span with the outcome now that we have the full picture
                Activity? turnSpan = MorganaTelemetry.Source.StartActivity(
                    MorganaTelemetry.RouterActivity,
                    ActivityKind.Internal,
                    ctx.TurnContext);
                turnSpan?.SetTag(MorganaTelemetry.RouterIntent, ctx.Classification?.Intent);
                turnSpan?.SetTag(MorganaTelemetry.RouterAgentPath, response.AgentRef.Path.ToString());
                turnSpan?.Dispose();

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
                    activeAgentIntent = ctx.Classification?.Intent;
                }

                ctx.OriginalSender.Tell(new Records.ConversationResponse(
                    response.Response,
                    ctx.Classification?.Intent,
                    ctx.Classification?.Metadata,
                    agentName,
                    response.IsCompleted,
                    response.QuickReplies,
                    ctx.OriginalMessage.Timestamp,
                    response.RichCard));

                Become(Idle);
            }
            catch (Exception ex)
            {
                actorLogger.Error(ex, "Error processing ActiveAgentResponse");

                activeAgent = null;
                activeAgentIntent = null;

                ctx.OriginalSender.Tell(new Records.ConversationResponse(
                    "An error occurred while processing the response. Please try again.",
                    ctx.Classification?.Intent,
                    ctx.Classification?.Metadata,
                    GetAgentDisplayName(ctx.Classification?.Intent),
                    false,
                    null,
                    ctx.OriginalMessage.Timestamp,
                    null));

                Become(Idle);
            }
        });

        Receive<Records.AgentResponse>(response =>
        {
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
                    DateTime.UtcNow,
                    response.RichCard));

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
                    DateTime.UtcNow,
                    null));

                Become(Idle);
            }
        });

        RegisterCommonHandlers();
    }

    /// <summary>
    /// AwaitingFollowUpResponse state: waiting for active agent to process follow-up message.
    /// Routes messages directly to the active agent, bypassing classification.
    /// </summary>
    /// <param name="originalSender">Original sender reference for response routing</param>
    private void AwaitingFollowUpResponse(IActorRef originalSender)
    {
        actorLogger.Info("→ State: AwaitingFollowUpResponse");

        Context.SetReceiveTimeout(TimeSpan.FromSeconds(90));

        Receive<ReceiveTimeout>(_ =>
        {
            actorLogger.Error($"Timeout waiting for follow-up response from active agent (intent: {activeAgentIntent})");

            Context.SetReceiveTimeout(null);

            activeAgent = null;
            activeAgentIntent = null;

            originalSender.Tell(new Records.ConversationResponse(
                "I apologize, but the request took too long to process. Please try again.",
                null,
                null,
                "Morgana",
                false,
                null,
                DateTime.UtcNow,
                null));

            Become(Idle);
        });

        Receive<Records.AgentStreamChunk>(chunk =>
        {
            Context.SetReceiveTimeout(TimeSpan.FromSeconds(90));
            originalSender.Tell(chunk);
        });

        Receive<Records.AgentResponse>(response =>
        {
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
                    DateTime.UtcNow,
                    response.RichCard));

                Become(Idle);
            }
            catch (Exception ex)
            {
                actorLogger.Error(ex, "Error processing follow-up AgentResponse");

                activeAgent = null;
                activeAgentIntent = null;

                originalSender.Tell(new Records.ConversationResponse(
                    "An error occurred while processing the response. Please try again.",
                    null,
                    null,
                    "Morgana",
                    false,
                    null,
                    DateTime.UtcNow,
                    null));

                Become(Idle);
            }
        });

        RegisterCommonHandlers();
    }

    #endregion

    #region Helper Methods

    private string GetAgentDisplayName(string? intent)
    {
        if (string.IsNullOrEmpty(intent) || string.Equals(intent, "other", StringComparison.OrdinalIgnoreCase))
            return "Morgana";

        string capitalizedIntent = char.ToUpper(intent[0]) + intent[1..];
        return $"Morgana ({capitalizedIntent})";
    }

    private void HandleUnexpectedFailure(Records.FailureContext failure)
    {
        actorLogger.Error(failure.Failure.Cause, "Unexpected failure in ConversationSupervisorActor");
        failure.OriginalSender.Tell(
            new Records.ConversationResponse("An internal error occurred.", null, null, "Morgana", false));
    }

    private void HandleRestoreActiveAgent(Records.RestoreActiveAgent msg)
    {
        actorLogger.Info($"Restoring active agent: {msg.AgentIntent}");

        if (string.Equals(msg.AgentIntent, "Morgana", StringComparison.OrdinalIgnoreCase))
        {
            activeAgent = null;
            activeAgentIntent = null;
            actorLogger.Info($"No active agent detected: fallback to Morgana");
            return;
        }

        router.Tell(new Records.RestoreAgentRequest(msg.AgentIntent));
    }

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
            activeAgent = null;
            activeAgentIntent = null;
            actorLogger.Warning($"Could not restore agent for intent '{response.AgentIntent}' - clearing active agent");
        }
    }

    /// <inheritdoc/>
    protected override void RegisterCommonHandlers()
    {
        base.RegisterCommonHandlers();

        Receive<Records.RestoreActiveAgent>(HandleRestoreActiveAgent);
        Receive<Records.RestoreAgentResponse>(HandleRestoreAgentResponse);
        Receive<Records.FailureContext>(HandleUnexpectedFailure);
    }

    #endregion
}