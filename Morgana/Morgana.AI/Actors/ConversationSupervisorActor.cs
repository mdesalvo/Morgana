using System.Diagnostics;
using Akka.Actor;
using Akka.Event;
using Microsoft.Extensions.Configuration;
using Morgana.AI.Abstractions;
using Morgana.AI.Extensions;
using Morgana.AI.Interfaces;
using Morgana.AI.Telemetry;
using Status = Akka.Actor.Status;

namespace Morgana.AI.Actors;

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
    private readonly IChannelService channelService;
    private readonly IAgentConfigurationService agentConfigService;
    private readonly IPresenterService presenterService;

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

    /// <summary>OTel root span covering the full turn pipeline (opened on UserMessage, closed on return to Idle).</summary>
    private Activity? _turnSpan;

    /// <summary>OTel span covering the guard-check duration (opened before Tell, closed on response).</summary>
    private Activity? _guardSpan;

    /// <summary>OTel span covering the classification duration (opened before Tell, closed on response).</summary>
    private Activity? _classifierSpan;

    /// <summary>
    /// Initializes a new instance of the ConversationSupervisorActor.
    /// Creates child actors (guard, classifier, router) and enters Idle state.
    /// </summary>
    public ConversationSupervisorActor(
        string conversationId,
        ILLMService llmService,
        IPromptResolverService promptResolverService,
        IChannelService channelService,
        IAgentConfigurationService agentConfigService,
        IPresenterService presenterService,
        IConfiguration configuration) : base(conversationId, llmService, promptResolverService, configuration)
    {
        this.channelService = channelService;
        this.agentConfigService = agentConfigService;
        this.presenterService = presenterService;

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

            // Open the turn span in the supervisor so it lives for the full pipeline duration.
            // Use an ActivityLink (not parent) to the HTTP span — the turn is async and outlives the request.
            ActivityLink[] links = msg.TurnContext != default
                ? [new ActivityLink(msg.TurnContext)]
                : [];

            _turnSpan = MorganaTelemetry.Source.StartActivity(MorganaTelemetry.TurnActivity, ActivityKind.Internal, parentContext: default, links: links);
            _turnSpan?.SetTag(MorganaTelemetry.ConversationId, conversationId);
            _turnSpan?.SetTag(MorganaTelemetry.TurnUserMessage, msg.Text.Length > 200 ? msg.Text[..200] : msg.Text);

            // Use the turn span's context as parent for all child spans (guard, classifier, agent)
            ActivityContext turnContext = _turnSpan?.Context ?? default;

            Records.ProcessingContext ctx = new Records.ProcessingContext(
                msg, originalSender, TurnContext: turnContext);

            // Open guard span before sending to GuardActor so it captures the full check duration
            _guardSpan = MorganaTelemetry.Source.StartActivity(MorganaTelemetry.GuardActivity, ActivityKind.Internal, turnContext);
            _guardSpan?.SetTag(MorganaTelemetry.ConversationId, conversationId);

            Become(() => AwaitingGuardCheck(ctx));

            guard.Tell(new Records.GuardCheckRequest(msg.ConversationId, msg.Text));
        });

        RegisterCommonHandlers();
    }

    /// <summary>
    /// Handles presentation generation requests.
    /// Loads displayable intents then delegates entirely to <see cref="IPresenterService"/>.
    /// </summary>
    private async Task HandlePresentationRequestAsync(Records.GeneratePresentationMessage _)
    {
        if (hasPresented)
        {
            actorLogger.Info("Presentation already shown, skipping");
            return;
        }

        hasPresented = true;
        actorLogger.Info("Generating presentation message via IPresenterService");

        List<Records.IntentDefinition> allIntents = await agentConfigService.GetIntentsAsync();
        Records.IntentCollection intentCollection = new Records.IntentCollection(allIntents);
        List<Records.IntentDefinition> displayableIntents = intentCollection.GetDisplayableIntents();

        Records.PresentationResult result = await presenterService.GenerateAsync(displayableIntents);

        Self.Tell(new Records.PresentationContext(result.Message, displayableIntents)
        {
            LLMQuickReplies = result.QuickReplies
        });
    }

    /// <summary>
    /// Handles the generated presentation and sends it to the client via SignalR.
    /// </summary>
    private async Task HandlePresentationGenerated(Records.PresentationContext ctx)
    {
        actorLogger.Info("Sending presentation to client via channel");

        List<Records.QuickReply> quickReplies = ctx.LLMQuickReplies?
            .Select(qr => new Records.QuickReply(qr.Id, qr.Label, qr.Value))
            .ToList() ?? [];

        try
        {
            await Task.Delay(750);

            await channelService.SendMessageAsync(new Records.ChannelMessage
            {
                ConversationId = conversationId,
                Text = ctx.Message,
                MessageType = "presentation",
                QuickReplies = quickReplies,
                AgentName = "Morgana",
                AgentCompleted = false
            });

            actorLogger.Info("Presentation sent successfully");
        }
        catch (Exception ex)
        {
            actorLogger.Error(ex, "Failed to send presentation via channel");
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
            // Close the guard span opened before Tell — now captures full check duration
            _guardSpan?.SetTag(MorganaTelemetry.GuardCompliant, response.Compliant);
            if (!response.Compliant && response.Violation != null)
                _guardSpan?.SetTag(MorganaTelemetry.GuardViolation, response.Violation);
            if (_guardSpan is not null)
                MorganaTelemetry.GuardDuration.Record((DateTime.UtcNow - _guardSpan.StartTimeUtc).TotalMilliseconds);
            _guardSpan?.Dispose();
            _guardSpan = null;

            if (!response.Compliant)
            {
                actorLogger.Warning($"Message rejected by guard: {response.Violation}");

                Records.Prompt guardPrompt = await promptResolverService.ResolveAsync("Guard");
                string guardAnswer = guardPrompt.GetAdditionalProperty<string>("GuardAnswer")
                    .Replace("((violation))", response.Violation ?? "Content policy violation");

                string currentAgentName = activeAgentIntent != null ? GetAgentDisplayName(activeAgentIntent) : "Morgana";
                ctx.OriginalSender.Tell(new Records.ConversationResponse(
                    guardAnswer, ctx.Classification?.Intent, null, currentAgentName, false));

                MorganaTelemetry.GuardRejectionCounter.Add(1);
                CloseTurnSpan(intent: ctx.Classification?.Intent, completed: false);
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
                    ctx.TurnContext,          // propagate context to agent
                    channelService.Capabilities));
            }
            else
            {
                actorLogger.Info("No active agent, proceeding to classification for new request");

                // Open classifier span before sending to ClassifierActor
                _classifierSpan = MorganaTelemetry.Source.StartActivity(MorganaTelemetry.ClassifierActivity, ActivityKind.Internal, ctx.TurnContext);
                _classifierSpan?.SetTag(MorganaTelemetry.ConversationId, conversationId);

                Become(() => AwaitingClassification(ctx));

                classifier.Tell(ctx.OriginalMessage);
            }
        });

        Receive<Status.Failure>(failure =>
        {
            actorLogger.Error(failure.Cause, "Guard check failed");
            actorLogger.Warning("Guard check failed, failing open (allowing message)");

            _guardSpan?.SetStatus(ActivityStatusCode.Error, failure.Cause.Message);
            _guardSpan?.AddException(failure.Cause);
            _guardSpan?.Dispose();
            _guardSpan = null;

            if (activeAgent != null)
            {
                Become(() => AwaitingFollowUpResponse(ctx.OriginalSender));

                activeAgent.Tell(new Records.AgentRequest(
                    ctx.OriginalMessage.ConversationId,
                    ctx.OriginalMessage.Text,
                    null,
                    ctx.TurnContext,
                    channelService.Capabilities));
            }
            else
            {
                // Open classifier span before sending to ClassifierActor
                _classifierSpan = MorganaTelemetry.Source.StartActivity(
                    MorganaTelemetry.ClassifierActivity,
                    ActivityKind.Internal,
                    ctx.TurnContext);
                _classifierSpan?.SetTag(MorganaTelemetry.ConversationId, conversationId);

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

            // Close the classifier span opened before Tell — now captures full classification duration
            _classifierSpan?.SetTag(MorganaTelemetry.ClassificationIntent, classification.Intent);
            if (classification.Metadata.TryGetValue("confidence", out string? confidence))
                _classifierSpan?.SetTag(MorganaTelemetry.ClassificationConfidence, confidence);
            if (_classifierSpan is not null)
                MorganaTelemetry.ClassifierDuration.Record((DateTime.UtcNow - _classifierSpan.StartTimeUtc).TotalMilliseconds);
            _classifierSpan?.Dispose();
            _classifierSpan = null;

            Records.ProcessingContext updatedCtx = ctx with { Classification = classification };

            // Emit router span marking the routing decision (agent selection happens inside RouterActor)
            using Activity? routerSpan = MorganaTelemetry.Source.StartActivity(
                MorganaTelemetry.RouterActivity,
                ActivityKind.Internal,
                ctx.TurnContext);
            routerSpan?.SetTag(MorganaTelemetry.RouterIntent, classification.Intent);

            Become(() => AwaitingAgentResponse(updatedCtx));

            router.Tell(new Records.AgentRequest(
                ctx.OriginalMessage.ConversationId,
                ctx.OriginalMessage.Text,
                classification,
                ctx.TurnContext,              // propagate context to router → agent
                channelService.Capabilities));
        });

        Receive<Status.Failure>(failure =>
        {
            actorLogger.Error(failure.Cause, "Classification failed");

            // Close classifier span with error status
            _classifierSpan?.SetStatus(ActivityStatusCode.Error, failure.Cause.Message);
            _classifierSpan?.AddException(failure.Cause);
            _classifierSpan?.Dispose();
            _classifierSpan = null;

            Records.ClassificationResult fallbackClassification = new Records.ClassificationResult(
                "other",
                new Dictionary<string, string>
                {
                    ["confidence"] = "0.00",
                    ["error"] = $"classification_failed: {failure.Cause.Message}"
                });

            actorLogger.Info("Falling back to 'other' intent");
            Records.ProcessingContext updatedCtx = ctx with { Classification = fallbackClassification };

            // Emit router span marking the fallback routing decision
            using Activity? routerSpan = MorganaTelemetry.Source.StartActivity(
                MorganaTelemetry.RouterActivity,
                ActivityKind.Internal,
                ctx.TurnContext);
            routerSpan?.SetTag(MorganaTelemetry.RouterIntent, fallbackClassification.Intent);

            Become(() => AwaitingAgentResponse(updatedCtx));

            router.Tell(new Records.AgentRequest(
                ctx.OriginalMessage.ConversationId,
                ctx.OriginalMessage.Text,
                fallbackClassification,
                ctx.TurnContext,
                channelService.Capabilities));
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

        Context.SetReceiveTimeout(TimeSpan.FromSeconds(Convert.ToInt32(configuration["Morgana:ActorSystem:TimeoutSeconds"])));

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

            CloseTurnSpan(ActivityStatusCode.Error, "Timeout waiting for agent response", intent: ctx.Classification?.Intent, completed: false);
            Become(Idle);
        });

        Receive<Records.AgentStreamChunk>(chunk =>
        {
            Context.SetReceiveTimeout(TimeSpan.FromSeconds(Convert.ToInt32(configuration["Morgana:ActorSystem:TimeoutSeconds"])));
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

                CloseTurnSpan(intent: ctx.Classification?.Intent, completed: response.IsCompleted);
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

                CloseTurnSpan(ActivityStatusCode.Error, ex.Message, intent: ctx.Classification?.Intent, completed: false, exception: ex);
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

                CloseTurnSpan(intent: ctx.Classification?.Intent, completed: true);
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

                CloseTurnSpan(ActivityStatusCode.Error, ex.Message, intent: ctx.Classification?.Intent, completed: false, exception: ex);
                Become(Idle);
            }
        });

        ReceiveAsync<Records.ContentFilterRejection>(_ => HandleContentFilterRejectionAsync(ctx.OriginalSender));

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

        Context.SetReceiveTimeout(TimeSpan.FromSeconds(Convert.ToInt32(configuration["Morgana:ActorSystem:TimeoutSeconds"])));

        Receive<ReceiveTimeout>(_ =>
        {
            actorLogger.Error($"Timeout waiting for follow-up response from active agent (intent: {activeAgentIntent})");

            Context.SetReceiveTimeout(null);

            string? timedOutIntent = activeAgentIntent;
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

            CloseTurnSpan(ActivityStatusCode.Error, "Timeout waiting for follow-up response", intent: timedOutIntent, completed: false);
            Become(Idle);
        });

        Receive<Records.AgentStreamChunk>(chunk =>
        {
            Context.SetReceiveTimeout(TimeSpan.FromSeconds(Convert.ToInt32(configuration["Morgana:ActorSystem:TimeoutSeconds"])));
            originalSender.Tell(chunk);
        });

        Receive<Records.AgentResponse>(response =>
        {
            Context.SetReceiveTimeout(null);

            string? currentIntent = activeAgentIntent;
            try
            {
                string agentName = currentIntent != null ? GetAgentDisplayName(currentIntent) : "Morgana";

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

                CloseTurnSpan(intent: currentIntent, completed: response.IsCompleted);
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

                CloseTurnSpan(ActivityStatusCode.Error, ex.Message, intent: currentIntent, completed: false, exception: ex);
                Become(Idle);
            }
        });

        ReceiveAsync<Records.ContentFilterRejection>(_ => HandleContentFilterRejectionAsync(originalSender));

        RegisterCommonHandlers();
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Closes the turn span, records metrics, and transitions to Idle.
    /// Called at every point where the FSM returns to Idle after processing a user message.
    /// </summary>
    private void CloseTurnSpan(
        ActivityStatusCode status = ActivityStatusCode.Ok,
        string? description = null,
        string? intent = null,
        bool? completed = null,
        Exception? exception = null)
    {
        if (_turnSpan is not null)
        {
            if (status == ActivityStatusCode.Error)
            {
                _turnSpan.SetStatus(status, description);
                if (exception is not null)
                    _turnSpan.AddException(exception);
            }

            double durationMs = (_turnSpan.Duration != TimeSpan.Zero)
                ? _turnSpan.Duration.TotalMilliseconds
                : (DateTime.UtcNow - _turnSpan.StartTimeUtc).TotalMilliseconds;

            MorganaTelemetry.TurnDuration.Record(durationMs);
            MorganaTelemetry.TurnCounter.Add(1,
                new KeyValuePair<string, object?>("intent", intent ?? "unknown"),
                new KeyValuePair<string, object?>("completed", completed ?? false));

            _turnSpan.Dispose();
            _turnSpan = null;
        }
    }

    /// <summary>
    /// Handles a content filter rejection from an agent as if it were a guard rejection.
    /// Uses the same GuardAnswer template and increments the guard rejection counter.
    /// </summary>
    private async Task HandleContentFilterRejectionAsync(IActorRef originalSender)
    {
        Context.SetReceiveTimeout(null);

        actorLogger.Warning("Content filter rejection received from agent, treating as guard rejection");

        Records.Prompt guardPrompt = await promptResolverService.ResolveAsync("Guard");
        string guardAnswer = guardPrompt.GetAdditionalProperty<string>("GuardAnswer")
            .Replace("((violation))", "Content policy violation");

        string currentAgentName = activeAgentIntent != null ? GetAgentDisplayName(activeAgentIntent) : "Morgana";
        originalSender.Tell(new Records.ConversationResponse(
            guardAnswer,
            activeAgentIntent,
            null,
            currentAgentName,
            false,
            null,
            DateTime.UtcNow,
            null));

        MorganaTelemetry.GuardRejectionCounter.Add(1);
        CloseTurnSpan(intent: activeAgentIntent, completed: false);
        Become(Idle);
    }

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
            new Records.ConversationResponse(
                "An internal error occurred.",
                null,
                null,
                "Morgana",
                false,
                null,
                DateTime.UtcNow,
                null));
    }

    private void HandleRestoreActiveAgent(Records.RestoreActiveAgent msg)
    {
        actorLogger.Info($"Restoring active agent: {msg.AgentIntent}");

        if (string.Equals(msg.AgentIntent, "Morgana", StringComparison.OrdinalIgnoreCase))
        {
            activeAgent = null;
            activeAgentIntent = null;
            actorLogger.Info("No active agent detected: fallback to Morgana");
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