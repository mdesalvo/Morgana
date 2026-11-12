using System.Diagnostics;
using System.Globalization;
using Akka.Actor;
using Akka.Event;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Morgana.AI.Abstractions;
using Morgana.AI.Extensions;
using Morgana.AI.Interfaces;
using Morgana.Contracts;
using Status = Akka.Actor.Status;

namespace Morgana.AI.Actors;

/// <summary>
/// The conversation's orchestrator: it takes each user message through the guard, the classifier and an agent.
/// Five states, Idle, AwaitingGuardCheck, AwaitingClassification, AwaitingAgentResponse and AwaitingFollowUpResponse:
/// the agent left mid-exchange is remembered across turns, so its follow-ups skip the classifier.
/// </summary>
public class ConversationSupervisorActor : MorganaActor
{
    /// <summary>The adapting channel, which delivers the welcome: every other answer travels back through the manager.</summary>
    private readonly IChannelService channelService;

    /// <summary>The conversation's channel, read for the turn's capabilities and for the last answer delivered.</summary>
    private readonly IChannelMetadataStore channelMetadataStore;

    /// <summary>The configured intents, read for the welcome, for the landing buttons and for the disambiguation.</summary>
    private readonly IAgentConfigurationService agentConfigService;

    /// <summary>Writes the welcome message and its buttons.</summary>
    private readonly IPresenterService presenterService;

    /// <summary>
    /// Holds which agent the conversation was left talking to, the one fact this supervisor must read back before its first turn.
    /// A process that restarted mid-exchange knows it from nowhere else.
    /// </summary>
    private readonly IConversationPersistenceService conversationPersistenceService;

    /// <summary>The conversation's guard, which judges every user message before anything else reads it.</summary>
    private readonly IActorRef guard;

    /// <summary>The conversation's classifier, which ranks the intents of a message no agent is waiting for.</summary>
    private readonly IActorRef classifier;

    /// <summary>The conversation's router, which brings up the agent of a classified intent.</summary>
    private readonly IActorRef router;

    /// <summary>The agent left mid-exchange, which receives the next message directly; null when Morgana holds the conversation.</summary>
    private IActorRef? activeAgent;

    /// <summary>The intent of the active agent, which names it to the client; null when Morgana holds the conversation.</summary>
    private string? activeAgentIntent;

    /// <summary>
    /// Whether this supervisor has already taken the active agent from the conversation's record.
    /// Read once, on its first turn: from then on this supervisor is the one keeping it current.
    /// </summary>
    private bool hasReadPersistedActiveAgent;

    /// <summary>Whether the welcome has been delivered, so a repeated presentation request does not greet the user again.</summary>
    private bool hasPresented;

    /// <summary>The root span of the running turn, opened on the user message and closed when the turn ends.</summary>
    private Activity? turnSpan;

    /// <summary>The span of the guard's round-trip, opened before the request and closed on its outcome.</summary>
    private Activity? guardSpan;

    /// <summary>The span of the classifier's round-trip, opened before the request and closed on its outcome.</summary>
    private Activity? classifierSpan;

    /// <summary>Stops the work of the phase that the supervisor is waiting on.</summary>
    private CancellationTokenSource? phaseCancellation;

    /// <summary>Binds the supervisor to its conversation, brings up its guard, classifier and router, then waits in Idle.</summary>
    public ConversationSupervisorActor(
        string conversationId,
        ILanguageModelService llmService,
        IPromptResolverService promptResolverService,
        IChannelService channelService,
        IChannelMetadataStore channelMetadataStore,
        IAgentConfigurationService agentConfigService,
        IPresenterService presenterService,
        IConversationPersistenceService conversationPersistenceService,
        IConfiguration configuration) : base(conversationId, llmService, promptResolverService, configuration)
    {
        this.channelService = channelService;
        this.channelMetadataStore = channelMetadataStore;
        this.agentConfigService = agentConfigService;
        this.presenterService = presenterService;
        this.conversationPersistenceService = conversationPersistenceService;

        // The three actors of the pipeline exist before the first message arrives, each named after this conversation.
        guard = Context.System.GetOrCreateActorAsync<GuardActor>(
            Constants.Actors.Guard, conversationId).GetAwaiter().GetResult();

        classifier = Context.System.GetOrCreateActorAsync<ClassifierActor>(
            Constants.Actors.Classifier, conversationId).GetAwaiter().GetResult();

        router = Context.System.GetOrCreateActorAsync<RouterActor>(
            Constants.Actors.Router, conversationId).GetAwaiter().GetResult();

        // A new supervisor has no turn running, so it waits for the first message.
        Idle();
    }

    #region State Behaviors

    /// <summary>
    /// Idle: waits for the welcome request or for a user message.
    /// Every user message goes through the guard first, the follow-ups of an active agent included.
    /// </summary>
    private void Idle()
    {
        actorLogger.Info("↗ State: Idle");

        // Idle is the one state that may wait indefinitely: every other one waits on work in flight that must not hang.
        Context.SetReceiveTimeout(null);

        // The manager asks for the welcome on every start of the conversation.
        ReceiveAsync<Records.GeneratePresentationMessage>(HandlePresentationRequestAsync);

        // The welcome comes back here once written, to be delivered over the channel.
        ReceiveAsync<Records.PresentationContext>(HandlePresentationGenerated);

        // A user message starts a turn.
        ReceiveAsync<Records.UserMessage>(HandleUserMessageAsync);

        RegisterCommonHandlers();
    }

    /// <summary>Starts a turn: it lands a press of Morgana's button on its agent and sends every other message to the guard.</summary>
    private async Task HandleUserMessageAsync(Records.UserMessage msg)
    {
        // The manager of the conversation, which delivers every answer of the turn.
        IActorRef originalSender = Sender;

        actorLogger.Info("User message received, routing through guard check");

        // Settled before the turn starts, because the turn decides on it: with an agent left
        // mid-exchange this message is its answer, never a new request to classify.
        if (!hasReadPersistedActiveAgent)
        {
            await RestorePersistedActiveAgentAsync();
            hasReadPersistedActiveAgent = true;
        }

        // With no agent mid-exchange the phrase is addressed to Morgana, so she files it now, before the guard has seen it:
        // a client that reloads during the guard or the classifier reads back what it just said.
        // An agent that was already active saves the phrase into its own session instead, because that session is its record.
        bool noActiveAgent = activeAgent == null;
        if (noActiveAgent)
            await conversationPersistenceService.AppendOrchestratorMessagesAsync(
                conversationId,
                [new ChatMessage(ChatRole.User, msg.Text) { CreatedAt = DateTimeOffset.UtcNow }]);

        // The channel's budget for everything this turn produces, carried with the turn to each
        // agent request it ends up sending.
        ChannelMetadata channelMetadata = await channelMetadataStore.GetChannelMetadataAsync(conversationId);
        ChannelCapabilities turnCapabilities = channelMetadata.Capabilities;

        // "morgana.turn" is the root span of the turn, under which the guard, the classifier, the router and the agent record theirs.
        // It is linked to the HTTP request's span rather than parented by it: the request has already answered the client
        // while the turn is still running, so a parent closing before its child would be the wrong shape.
        ActivityLink[] requestLinks = msg.TurnContext != default ? [new ActivityLink(msg.TurnContext)] : [];
        turnSpan = Telemetry.Source.StartActivity(Telemetry.TurnActivity, ActivityKind.Internal, parentContext: default, links: requestLinks);
        turnSpan?.SetTag(Telemetry.ConversationId, conversationId);
        turnSpan?.SetTag(Telemetry.TurnUserMessage, msg.Text.Length > 200 ? msg.Text[..200] : msg.Text);
        ActivityContext turnContext = turnSpan?.Context ?? default;

        // A press of a button that Morgana offered to choose an agent lands the turn on that agent: the
        // press is the choice, so neither the guard nor the classifier has anything left to decide.
        string? landingIntent = noActiveAgent ? await FindLandingIntentAsync(msg.Text) : null;
        if (landingIntent is not null)
        {
            actorLogger.Info($"Landing on intent '{landingIntent}': the message is the press of Morgana's button");

            // A certain classification, so the rest of the turn reads exactly as a classified one does.
            Records.ClassificationResult landingClassification = new Records.ClassificationResult(
                landingIntent, new Dictionary<string, string> { [Constants.ClassificationMetadata.Confidence] = "1.00" });
            Records.ProcessingContext landingContext = new Records.ProcessingContext(
                msg, originalSender, turnCapabilities, landingClassification, turnContext, UserMessageAlreadyStored: noActiveAgent);

            // The same "morgana.router" span a classified turn opens, covering the round-trip to the agent.
            using Activity? landingRouterSpan = Telemetry.Source.StartActivity(
                Telemetry.RouterActivity,
                ActivityKind.Internal,
                turnContext);
            landingRouterSpan?.SetTag(Telemetry.RouterIntent, landingIntent);

            // The supervisor now waits for the chosen agent, as after a classification.
            Become(() => AwaitingAgentResponse(landingContext));

            // The router reaches the chosen agent, which is told the turn is its landing so that it presents itself.
            router.Tell(new Records.AgentRequest(
                msg.ConversationId,
                msg.Text,
                landingClassification,
                turnContext,
                turnCapabilities,
                noActiveAgent,
                IsLanding: true,
                TurnCancellation: StartPhaseCancellation()));
            return;
        }

        // "morgana.guard" gives the guard's latency and verdict their own row in a trace.
        // It opens before the request, so its duration covers the whole round-trip, the wait in the guard's mailbox included.
        guardSpan = Telemetry.Source.StartActivity(Telemetry.GuardActivity, ActivityKind.Internal, turnContext);
        guardSpan?.SetTag(Telemetry.ConversationId, conversationId);

        // The supervisor now waits for the verdict, carrying the turn's context into the next phases.
        Records.ProcessingContext ctx = new Records.ProcessingContext(
            msg, originalSender, turnCapabilities, TurnContext: turnContext, UserMessageAlreadyStored: noActiveAgent);
        Become(() => AwaitingGuardCheck(ctx));

        // The guard judges the user's phrase before anything else reads it.
        guard.Tell(new Records.GuardCheckRequest(msg.ConversationId, msg.Text, TurnCancellation: StartPhaseCancellation()));
    }

    /// <summary>
    /// Finds the intent that the user's message chooses when it is the press of a button on the last answer delivered to the user.
    /// </summary>
    /// <param name="userText">What the user sent: a pressed button sends its own value.</param>
    /// <returns>The configured intent that the pressed button names; <c>null</c> when the message is not such a press.</returns>
    private async Task<string?> FindLandingIntentAsync(string userText)
    {
        // The buttons on screen are those of the last answer the channel delivered, as the user received it;
        // after a restart none is held, so a press reaches the classifier as an ordinary request.
        ChannelMessage? lastAnswer = await channelMetadataStore.GetLastAnswerAsync(conversationId);
        List<QuickReply>? offeredButtons = lastAnswer?.QuickReplies;

        // A last answer with no buttons leaves the message an ordinary request: a farewell offers none
        // and a channel that shows none receives none.
        if (offeredButtons is null || offeredButtons.Count == 0)
            return null;

        // A press sends the button's own value, so the text that matches an offered value is that button.
        QuickReply? pressedButton = offeredButtons.FirstOrDefault(button =>
            string.Equals(button.Value, userText.Trim(), StringComparison.OrdinalIgnoreCase));

        // Typed text that matches no offered button is an ordinary request.
        if (pressedButton is null)
            return null;

        // Only a button naming a configured intent chooses an agent: Morgana's welcome, her reintroduction and her
        // disambiguation offer such buttons and nothing else she says does.
        List<Records.IntentDefinition> configuredIntents = await agentConfigService.GetIntentsAsync();
        return configuredIntents
            .FirstOrDefault(intent => string.Equals(intent.Name, pressedButton.Id, StringComparison.OrdinalIgnoreCase))?.Name;
    }

    /// <summary>Writes the welcome on the intents a user can choose and hands it back to the supervisor for delivery.</summary>
    private async Task HandlePresentationRequestAsync(Records.GeneratePresentationMessage _)
    {
        // Neither a resumed conversation whose supervisor was already alive nor a duplicate request may greet the user again mid-conversation.
        if (hasPresented)
        {
            actorLogger.Info("Presentation already shown, skipping");
            return;
        }

        // The welcome is due once in the supervisor's life, whatever the presenter writes.
        hasPresented = true;
        actorLogger.Info("Generating presentation message via IPresenterService");

        // The welcome offers a button only for an intent that has one to show: Other and the label-less intents carry none.
        List<Records.IntentDefinition> configuredIntents = await agentConfigService.GetIntentsAsync();
        Records.IntentCollection intentCollection = new Records.IntentCollection(configuredIntents);
        List<Records.IntentDefinition> displayableIntents = intentCollection.GetDisplayableIntents();

        // The presenter writes the welcome by model or from configuration and never throws.
        Records.PresentationResult presentation = await presenterService.GenerateAsync(displayableIntents, conversationId);

        // Delivery runs as its own message, so a channel outage is never mistaken for a failure to write the welcome.
        Self.Tell(new Records.PresentationContext(presentation.Message, displayableIntents)
        {
            QuickReplies = presentation.QuickReplies
        });
    }

    /// <summary>Records the welcome as Morgana's first word and delivers it over the channel.</summary>
    private async Task HandlePresentationGenerated(Records.PresentationContext ctx)
    {
        actorLogger.Info("Sending presentation to client via channel");

        // A welcome button chooses an agent and never ends anything, so only its id, label and value are carried over.
        List<QuickReply> quickReplies = ctx.QuickReplies?
            .Select(quickReply => new QuickReply(quickReply.Id, quickReply.Label, quickReply.Value))
            .ToList() ?? [];

        try
        {
            // Dated once, so the record and the push agree and a returning client recognises the greeting it already has.
            DateTime presentationTimestamp = DateTime.UtcNow;

            // The welcome is Morgana's first word, filed in her own row.
            await conversationPersistenceService.AppendOrchestratorMessagesAsync(
                conversationId,
                [new ChatMessage(ChatRole.Assistant, ctx.Message) { CreatedAt = presentationTimestamp }]);

            // The adapting channel fits the welcome to the conversation's capabilities before the transport carries it.
            await channelService.SendMessageAsync(new ChannelMessage
            {
                ConversationId = conversationId,
                Text = ctx.Message,
                MessageType = ChannelMessageTypes.Presentation,
                QuickReplies = quickReplies,
                AgentName = Constants.Morgana,
                AgentCompleted = false,
                Timestamp = presentationTimestamp
            });

            actorLogger.Info("Presentation sent successfully");
        }
        catch (Exception ex)
        {
            // A failed delivery leaves the user without a welcome, never the conversation stuck: the first message still works.
            actorLogger.Error(ex, "Failed to send presentation via channel");
        }
    }

    /// <summary>AwaitingGuardCheck: waits for the guard's verdict, refusing the message when no verdict arrives.</summary>
    /// <param name="ctx">The turn so far: the user's message, who to answer and the turn's span context.</param>
    private void AwaitingGuardCheck(Records.ProcessingContext ctx)
    {
        actorLogger.Info("→ State: AwaitingGuardCheck");

        // The guard has the phase budget to answer, after which the message is refused.
        Context.SetReceiveTimeout(ActorSystemTimeout);

        // The verdict arrived: the failure and the silence of the guard are handled below.
        ReceiveAsync<Records.GuardRailResult>(async verdict => {
            // The guard's window closes on its answer, so it never carries over into the next state.
            Context.SetReceiveTimeout(null);

            // The guard answered, so its span closes on the verdict.
            CloseGuardSpan(verdict, null);

            // A refusal ends the turn here, with the active agent left as it was.
            if (!verdict.Compliant)
            {
                await RejectAsync(verdict);
                return;
            }

            actorLogger.Info("Message passed guard check");

            // An agent mid-exchange receives the message as its follow-up, with no classification.
            if (activeAgent != null)
            {
                actorLogger.Info($"Active agent exists, routing to follow-up flow with agent {activeAgent.Path}");

                // The supervisor now waits for the active agent's answer.
                Become(() => AwaitingFollowUpResponse(ctx.OriginalSender));

                // The agent receives the phrase with no classification, since none was made.
                activeAgent.Tell(new Records.AgentRequest(
                    ctx.OriginalMessage.ConversationId,
                    ctx.OriginalMessage.Text,
                    null,
                    ctx.TurnContext,
                    ctx.ChannelCapabilities,
                    ctx.UserMessageAlreadyStored,
                    TurnCancellation: StartPhaseCancellation()));
            }
            // With no agent mid-exchange the message is a new request, which the classifier ranks.
            else
            {
                actorLogger.Info("No active agent, proceeding to classification for new request");

                // "morgana.classifier" opens before the request, so its duration covers the whole round-trip as the guard's does.
                classifierSpan = Telemetry.Source.StartActivity(Telemetry.ClassifierActivity, ActivityKind.Internal, ctx.TurnContext);
                classifierSpan?.SetTag(Telemetry.ConversationId, conversationId);

                // The supervisor now waits for the classification.
                Become(() => AwaitingClassification(ctx));

                // The classifier reads the phrase that the guard has just admitted.
                classifier.Tell(ctx.OriginalMessage with { TurnCancellation = StartPhaseCancellation() });
            }
        });

        // A guard that threw reached no verdict, so the message is refused.
        ReceiveAsync<Status.Failure>(failure => FailClosedAsync(failure.Cause.GetType().FullName ?? failure.Cause.GetType().Name, failure.Cause));

        // A guard that stayed silent for the whole window reached no verdict either.
        ReceiveAsync<ReceiveTimeout>(async _ =>
        {
            // The guard the supervisor stops waiting for is stopped too, down to its model call.
            phaseCancellation?.Cancel();

            // The message is refused for want of a verdict.
            await FailClosedAsync(Telemetry.ErrorTypes.Timeout, null);
        });

        RegisterCommonHandlers();
        return;

        #region Utilities
        // The turn ends on a refusal, whoever decided it.
        async Task RejectAsync(Records.GuardRailResult verdict)
        {
            actorLogger.Warning($"Message rejected by guard: {verdict.Violation}");

            // With an agent mid-exchange nobody stored the phrase at ingress and the guard made sure the agent never runs.
            // Morgana answers this turn instead, so the phrase is hers to keep: without it the refusal would sit in
            // the transcript with nothing above it explaining what was refused.
            if (!ctx.UserMessageAlreadyStored)
                await conversationPersistenceService.AppendOrchestratorMessagesAsync(
                    conversationId,
                    [new ChatMessage(ChatRole.User, ctx.OriginalMessage.Text) { CreatedAt = DateTime.UtcNow }]);

            // The refusal is the whole reply, spoken by the active agent when there is one and by Morgana otherwise.
            ctx.OriginalSender.Tell(new Records.ConversationResponse(
                verdict.Violation!,
                ctx.Classification?.Intent,
                ctx.Classification?.Metadata,
                activeAgentIntent != null ? GetAgentDisplayName(activeAgentIntent) : Constants.Morgana,
                false,
                null,
                null,
                null));

            // The rejection counter counts judgments: a guard that could not judge rejected nothing.
            if (verdict.Source != Records.GuardVerdictSource.FailClosed)
                Telemetry.GuardRejectionCounter.Add(1);

            // The rejection at the user's door with the layer that decided it.
            TagList securityTags = new TagList
            {
                { Telemetry.SecurityDoor, Telemetry.SecurityDoors.User },
                { Telemetry.SecurityOutcome, Telemetry.SecurityOutcomes.Rejected },
                { Telemetry.SecuritySource, verdict.Source.ToString() }
            };

            // The technical cause is published only when the message was refused for want of a judgment.
            if (verdict.ErrorType is not null)
                securityTags.Add(Telemetry.ErrorType, verdict.ErrorType);

            // Counts the rejection at the user's door.
            Telemetry.SecurityEvents.Add(1, securityTags);

            // The turn ends uncompleted.
            CloseTurnSpan(intent: ctx.Classification?.Intent, completed: false);

            // The active agent stays as it was, so the next message reaches it after the guard.
            Become(Idle);
        }

        // Closes the guard span on a verdict or on a failure to reach one.
        void CloseGuardSpan(Records.GuardRailResult verdict, Exception? cause)
        {
            // Records which layer decided, so a trace tells a judgment from a refusal for lack of one.
            guardSpan?.SetTag(Telemetry.GuardSource, verdict.Source.ToString());

            // A guard that could not judge reached no verdict: the span carries the technical cause instead of a compliance finding.
            if (verdict.Source == Records.GuardVerdictSource.FailClosed)
            {
                // Marks the span as errored with the cause that stopped the judgment.
                guardSpan?.SetStatus(ActivityStatusCode.Error, verdict.ErrorType);
                guardSpan?.SetTag(Telemetry.ErrorType, verdict.ErrorType);

                // Attaches the exception itself where there was one.
                if (cause is not null)
                    guardSpan?.AddException(cause);
            }
            // A judgment carries its compliance and the finding the user was refused with.
            else
            {
                // Tracks the compliance of the judgment.
                guardSpan?.SetTag(Telemetry.GuardCompliant, verdict.Compliant);

                // Tracks the finding that the user was refused with.
                if (!verdict.Compliant && verdict.Violation != null)
                    guardSpan?.SetTag(Telemetry.GuardViolation, verdict.Violation);
            }

            // Records how long the guard took, from the span's own start.
            if (guardSpan is not null)
                Telemetry.GuardDuration.Record((DateTime.UtcNow - guardSpan.StartTimeUtc).TotalMilliseconds);

            // Ends the span and forgets it so no later path closes it twice.
            guardSpan?.Dispose();
            guardSpan = null;
        }

        // Shared fail-closed path for a GuardActor failure and a timeout. A message nobody judged never
        // reaches the classifier or an agent: the user is asked to send it again.
        async Task FailClosedAsync(string errorType, Exception? cause)
        {
            // The guard's window closes on its failure, so it never carries over into Idle.
            Context.SetReceiveTimeout(null);

            // Logs the failure itself and the "fail-closed" decision
            if (cause != null)
                actorLogger.Error(cause, "Guard check failed: {0}", errorType);
            else
                actorLogger.Error("Guard check failed: {0}", errorType);
            actorLogger.Warning("Guard check failed, failing closed (refusing message)");

            // The refusal the user reads, worded in the UserGuard prompt.
            string noVerdictRefusal = (await promptResolverService.ResolveAsync(Constants.Prompts.UserGuard))
                .GetAdditionalProperty<string>(Constants.PromptProperties.NoVerdictMessage);

            // The verdict of a guard that could not judge, carrying the technical cause.
            Records.GuardRailResult verdict = new Records.GuardRailResult(false, noVerdictRefusal, Records.GuardVerdictSource.FailClosed, errorType);

            // The guard span closes on the failure.
            CloseGuardSpan(verdict, cause);

            // Ends the turn on the refusal.
            await RejectAsync(verdict);
        }
        #endregion
    }

    /// <summary>AwaitingClassification: waits for the classifier's ranking, falling back to the Other intent when none arrives.</summary>
    /// <param name="ctx">The turn so far: the user's message, who to answer and the turn's span context.</param>
    private void AwaitingClassification(Records.ProcessingContext ctx)
    {
        actorLogger.Info("→ State: AwaitingClassification");

        // The classifier has the phase budget to answer, after which the turn falls back to the Other intent.
        Context.SetReceiveTimeout(ActorSystemTimeout);

        // The classification arrived: the failure and the silence of the classifier are handled below.
        ReceiveAsync<Records.ClassificationResult>(async classification => {
            // The classifier's window closes on its answer, so it never carries over into the next state.
            Context.SetReceiveTimeout(null);

            actorLogger.Info($"Classification result: {classification.Intent}");

            // The classifier span closes on the top intent, its confidence and the whole metadata.
            classifierSpan?.SetTag(Telemetry.ClassificationIntent, classification.Intent);
            classifierSpan?.SetTag(Telemetry.ClassificationMetadata, classification.Metadata);
            if (classification.Metadata.TryGetValue(Constants.ClassificationMetadata.Confidence, out string? confidence))
                classifierSpan?.SetTag(Telemetry.ClassificationConfidence, confidence);
            if (classifierSpan is not null)
                Telemetry.ClassifierDuration.Record((DateTime.UtcNow - classifierSpan.StartTimeUtc).TotalMilliseconds);
            classifierSpan?.Dispose();
            classifierSpan = null;

            // The turn carries the classification forward, to the agent or to the disambiguation.
            Records.ProcessingContext classifiedContext = ctx with { Classification = classification };

            // Two candidates within the collision threshold are offered to the user to choose: no agent runs and none becomes active.
            if (classification.Metadata.TryGetValue(Constants.ClassificationMetadata.AmbiguousIntents, out string? collidingIntentNames))
            {
                await SendDisambiguationAsync(classifiedContext, collidingIntentNames);
                return;
            }

            // "morgana.router" opens before the request, so its duration covers the whole round-trip to the agent.
            using Activity? routerSpan = Telemetry.Source.StartActivity(
                Telemetry.RouterActivity,
                ActivityKind.Internal,
                ctx.TurnContext);
            routerSpan?.SetTag(Telemetry.RouterIntent, classification.Intent);

            // The supervisor now waits for the agent of the classified intent.
            Become(() => AwaitingAgentResponse(classifiedContext));

            // The router brings up the agent of the classified intent and hands it the request.
            router.Tell(new Records.AgentRequest(
                ctx.OriginalMessage.ConversationId,
                ctx.OriginalMessage.Text,
                classification,
                ctx.TurnContext,
                ctx.ChannelCapabilities,
                ctx.UserMessageAlreadyStored,
                TurnCancellation: StartPhaseCancellation()));
        });

        // A classifier that threw found nothing, so the turn falls back to the Other intent.
        Receive<Status.Failure>(failure => FallbackToOther(failure.Cause.Message, failure.Cause));

        // A classifier that stayed silent for the whole window found nothing either.
        Receive<ReceiveTimeout>(_ =>
        {
            // The classification the supervisor stops waiting for is stopped too, down to its model call.
            phaseCancellation?.Cancel();

            // The turn falls back to the Other intent.
            FallbackToOther("receive timeout", null);
        });

        RegisterCommonHandlers();
        return;

        #region Utilities
        // Shared fallback to the Other intent for a classifier that threw and for one that stayed silent.
        void FallbackToOther(string description, Exception? cause)
        {
            // The classifier's window closes on its failure: AwaitingAgentResponse arms its own.
            Context.SetReceiveTimeout(null);

            // Logs the failure itself, with the exception attached when there is one.
            if (cause != null)
                actorLogger.Error(cause, "Classification failed: {0}", description);
            else
                actorLogger.Error("Classification failed: {0}", description);

            // The classifier span closes as errored, with the exception where there was one.
            classifierSpan?.SetStatus(ActivityStatusCode.Error, description);
            if (cause != null)
                classifierSpan?.AddException(cause);
            classifierSpan?.Dispose();
            classifierSpan = null;

            // Shaped like a real classification, so nothing downstream needs a case for the failure.
            // The classification-error key is the one a real classification never carries: the router reads it
            // to answer with the classification-error message.
            Records.ClassificationResult fallbackClassification = new Records.ClassificationResult(
                Constants.Intents.Other,
                new Dictionary<string, string>
                {
                    [Constants.ClassificationMetadata.Confidence] = "0.00",
                    [Constants.ClassificationMetadata.Error] = $"classification_failed: {description}"
                });

            actorLogger.Info("Falling back to 'other' intent");

            // The turn carries the fallback classification forward to the router.
            Records.ProcessingContext fallbackContext = ctx with { Classification = fallbackClassification };

            // The same "morgana.router" span as a real classification, covering the round-trip to the router.
            using Activity? routerSpan = Telemetry.Source.StartActivity(
                Telemetry.RouterActivity,
                ActivityKind.Internal,
                ctx.TurnContext);
            routerSpan?.SetTag(Telemetry.RouterIntent, fallbackClassification.Intent);

            // The supervisor now waits for the router's own answer.
            Become(() => AwaitingAgentResponse(fallbackContext));

            // No agent handles the Other intent, so the router answers with the classification-error message.
            router.Tell(new Records.AgentRequest(
                ctx.OriginalMessage.ConversationId,
                ctx.OriginalMessage.Text,
                fallbackClassification,
                ctx.TurnContext,
                ctx.ChannelCapabilities,
                ctx.UserMessageAlreadyStored,
                TurnCancellation: StartPhaseCancellation()));
        }
        #endregion
    }

    /// <summary>AwaitingAgentResponse: waits for the routed agent or for the router's own answer, relaying the stream meanwhile.</summary>
    /// <param name="ctx">The turn so far: the user's message, who to answer, the classification and the turn's span context.</param>
    private void AwaitingAgentResponse(Records.ProcessingContext ctx)
    {
        actorLogger.Info("→ State: AwaitingAgentResponse");

        // The router and its agent have the phase budget of silence: every chunk and every sign of work renews it.
        Context.SetReceiveTimeout(ActorSystemTimeout);

        // Neither the router nor its agent gave a sign of life for the whole window.
        Receive<ReceiveTimeout>(_ =>
        {
            // The router and the agent the supervisor stops waiting for are stopped too, down to their model call.
            phaseCancellation?.Cancel();

            actorLogger.Error($"Timeout waiting for agent response (classification: {ctx.Classification?.Intent})");

            // The window closes on its expiry, so it never fires again in the next state.
            Context.SetReceiveTimeout(null);

            // The user receives an apology. No agent was confirmed for this turn yet, so there is no active agent to drop,
            // unlike the timeout of AwaitingFollowUpResponse.
            ctx.OriginalSender.Tell(new Records.ConversationResponse(
                "I apologize, time ran out before the cauldron could brew your answer. Cast it again.",
                ctx.Classification?.Intent,
                ctx.Classification?.Metadata,
                GetAgentDisplayName(ctx.Classification?.Intent),
                false,
                null,
                null,
                null));

            // The turn ends as errored and uncompleted.
            CloseTurnSpan(ActivityStatusCode.Error, "Timeout waiting for agent response", intent: ctx.Classification?.Intent, completed: false);

            // The next message starts a new turn.
            Become(Idle);
        });

        // A streamed piece of the answer reaches the client as it arrives.
        Receive<Records.AgentStreamChunk>(chunk =>
        {
            // Every chunk renews the window: a slow stream that keeps producing never times out, only a stalled agent does.
            Context.SetReceiveTimeout(ActorSystemTimeout);

            // The chunk travels unchanged to the client.
            ctx.OriginalSender.Tell(chunk);
        });

        // Renews the same window for work the user cannot see: a tool running, a colleague answering.
        // The client is told nothing, having nothing to show; an agent that stops sending
        // these is one that has genuinely stopped.
        Receive<Records.AgentStillWorking>(_ => Context.SetReceiveTimeout(ActorSystemTimeout));

        // The router found and ran an agent for the classified intent.
        // AgentResponse below is the router's own answer when no agent handles the intent: the presentation or the classification error.
        ReceiveAsync<Records.ActiveAgentResponse>(async response =>
        {
            // The window closes on the agent's answer, since Idle arms none.
            Context.SetReceiveTimeout(null);

            try
            {
                // The answer is signed with the classified agent's name.
                string agentName = GetAgentDisplayName(ctx.Classification?.Intent);

                actorLogger.Info($"Received ActiveAgentResponse from {response.AgentRef.Path}, " +
                                 $"completed: {response.IsCompleted}, " +
                                 $"quickReplies: {response.QuickReplies?.Count ?? 0}");

                // The agent handed the conversation back, so Morgana holds it again.
                if (response.IsCompleted)
                {
                    actorLogger.Info("Agent signaled completion, clearing active agent");
                    activeAgent = null;
                    activeAgentIntent = null;
                }
                // The agent stays in service, so the next message reaches it directly.
                else
                {
                    actorLogger.Info($"Agent signaled incomplete, setting as active agent: {response.AgentRef.Path}");
                    activeAgent = response.AgentRef;
                    activeAgentIntent = ctx.Classification?.Intent;
                }

                // The user receives the agent's answer with its buttons, its card and the timestamp it is recorded under.
                // An agent that handed the conversation back without a word has nothing to deliver: Morgana's farewell below
                // is the whole answer.
                if (response.Response.Length > 0)
                {
                    ctx.OriginalSender.Tell(new Records.ConversationResponse(
                        response.Response,
                        ctx.Classification?.Intent,
                        ctx.Classification?.Metadata,
                        agentName,
                        response.IsCompleted,
                        response.QuickReplies,
                        response.RecordedTimestamp,
                        response.RichCard));
                }

                // The agent has finished: Morgana takes the conversation back and says so, behind
                // the answer above rather than in place of it.
                if (response.IsCompleted)
                {
                    // Morgana's closing line follows the agent's last answer.
                    TellAgentFarewell(ctx.OriginalSender, agentName);

                    // The farewell hands the conversation back to Morgana, so the departed agent's row leaves active duty in the same step.
                    // Awaited before the next message is read: a user returning at once to the same agent cannot have the new turn's active row switched off behind it.
                    if (ctx.Classification?.Intent is { Length: > 0 } departedIntent)
                        await conversationPersistenceService.DeactivateAgentAsync(conversationId, departedIntent);
                }

                // The turn ends, counted under the classified intent and the agent's completion.
                CloseTurnSpan(intent: ctx.Classification?.Intent, completed: response.IsCompleted);

                // The active agent, not the state, carries a follow-up forward.
                Become(Idle);
            }
            catch (Exception ex)
            {
                actorLogger.Error(ex, "Error processing ActiveAgentResponse");

                // An answer that could not be handled leaves no agent to keep talking to: the next message is classified afresh.
                activeAgent = null;
                activeAgentIntent = null;

                // The user receives an apology in place of the answer.
                ctx.OriginalSender.Tell(new Records.ConversationResponse(
                    "I apologize, the potion bubbled over in error. Repeat your incantation.",
                    ctx.Classification?.Intent,
                    ctx.Classification?.Metadata,
                    GetAgentDisplayName(ctx.Classification?.Intent),
                    false,
                    null,
                    null,
                    null));

                // The turn ends as errored, carrying the exception.
                CloseTurnSpan(ActivityStatusCode.Error, ex.Message, intent: ctx.Classification?.Intent, completed: false, exception: ex);

                // The next message starts a new turn.
                Become(Idle);
            }
        });

        // The router's own answer when no agent handles the classified intent.
        Receive<Records.AgentResponse>(response =>
        {
            // The window closes on the router's answer, since Idle arms none.
            Context.SetReceiveTimeout(null);

            try
            {
                actorLogger.Info("Received fallback response from router (no specialized agent)");

                // Morgana speaks the router's answer and the turn is completed by construction, since no agent stays behind it.
                ctx.OriginalSender.Tell(new Records.ConversationResponse(
                    response.Response,
                    ctx.Classification?.Intent,
                    ctx.Classification?.Metadata,
                    Constants.Morgana,
                    true,
                    response.QuickReplies,
                    null,
                    response.RichCard));

                // The turn ends completed.
                CloseTurnSpan(intent: ctx.Classification?.Intent, completed: true);

                // The next message starts a new turn.
                Become(Idle);
            }
            catch (Exception ex)
            {
                actorLogger.Error(ex, "Error processing fallback AgentResponse");

                // The user receives an apology in place of the router's answer.
                ctx.OriginalSender.Tell(new Records.ConversationResponse(
                    "I apologize, the grimoire slammed shut. Utter the words once more.",
                    ctx.Classification?.Intent,
                    ctx.Classification?.Metadata,
                    Constants.Morgana,
                    false,
                    null,
                    DateTime.UtcNow,
                    null));

                // The turn ends as errored, carrying the exception.
                CloseTurnSpan(ActivityStatusCode.Error, ex.Message, intent: ctx.Classification?.Intent, completed: false, exception: ex);

                // The next message starts a new turn.
                Become(Idle);
            }
        });

        // The provider refused the agent's own request on content grounds.
        ReceiveAsync<Records.ContentFilterRejection>(_ => HandleContentFilterRejectionAsync(ctx.OriginalSender));

        RegisterCommonHandlers();
    }

    /// <summary>AwaitingFollowUpResponse: waits for the active agent's answer to its follow-up, relaying the stream meanwhile.</summary>
    /// <param name="originalSender">Who receives the answer: the follow-up carries no classification to forward.</param>
    private void AwaitingFollowUpResponse(IActorRef originalSender)
    {
        actorLogger.Info("→ State: AwaitingFollowUpResponse");

        // The active agent has the phase budget of silence: every chunk and every sign of work renews it.
        Context.SetReceiveTimeout(ActorSystemTimeout);

        // The active agent gave no sign of life for the whole window.
        Receive<ReceiveTimeout>(_ =>
        {
            // The agent the supervisor stops waiting for is stopped too, down to its model call.
            phaseCancellation?.Cancel();

            actorLogger.Error($"Timeout waiting for follow-up response from active agent (intent: {activeAgentIntent})");

            // The window closes on its expiry, so it never fires again in the next state.
            Context.SetReceiveTimeout(null);

            // A silent agent is dropped, so the next message is classified as a new request.
            // Its intent is kept aside first, for the turn's count.
            string? timedOutIntent = activeAgentIntent;
            activeAgent = null;
            activeAgentIntent = null;

            // The user receives an apology from Morgana, with no intent or metadata to attach.
            originalSender.Tell(new Records.ConversationResponse(
                "I apologize, the sands of time drained from the cauldron. Re-weave your spell.",
                null,
                null,
                Constants.Morgana,
                false,
                null,
                DateTime.UtcNow,
                null));

            // The turn ends as errored, counted under the dropped agent's intent.
            CloseTurnSpan(ActivityStatusCode.Error, "Timeout waiting for follow-up response", intent: timedOutIntent, completed: false);

            // The next message starts a new turn.
            Become(Idle);
        });

        // A streamed piece of the answer reaches the client as it arrives.
        Receive<Records.AgentStreamChunk>(chunk =>
        {
            // Every chunk renews the window, as in AwaitingAgentResponse.
            Context.SetReceiveTimeout(ActorSystemTimeout);

            // The chunk travels unchanged to the client.
            originalSender.Tell(chunk);
        });

        // Renews the window for text-less work, as in AwaitingAgentResponse: the follow-up turn is where an active agent consults a colleague.
        Receive<Records.AgentStillWorking>(_ => Context.SetReceiveTimeout(ActorSystemTimeout));

        // The active agent answered its follow-up.
        ReceiveAsync<Records.AgentResponse>(async response =>
        {
            // The window closes on the agent's answer, since Idle arms none.
            Context.SetReceiveTimeout(null);

            // The intent is kept aside before completion clears it, for the farewell, the record and the turn's count.
            string? currentIntent = activeAgentIntent;
            try
            {
                // The answer is signed with the active agent's name.
                string agentName = currentIntent != null ? GetAgentDisplayName(currentIntent) : Constants.Morgana;

                // The agent handed the conversation back, so Morgana holds it again; otherwise it stays in service unchanged.
                if (response.IsCompleted)
                {
                    actorLogger.Info("Active agent signaled completion, clearing active agent");
                    activeAgent = null;
                    activeAgentIntent = null;
                }

                // The user receives the agent's answer, with no intent or metadata since the follow-up was not classified.
                // A hand-back without a word is answered by Morgana's farewell alone, as in AwaitingAgentResponse.
                if (response.Response.Length > 0)
                {
                    originalSender.Tell(new Records.ConversationResponse(
                        response.Response,
                        null,
                        null,
                        agentName,
                        response.IsCompleted,
                        response.QuickReplies,
                        response.RecordedTimestamp,
                        response.RichCard));
                }

                // The agent has finished: Morgana takes the conversation back and says so, behind
                // the answer above rather than in place of it.
                if (response.IsCompleted)
                {
                    // Morgana's closing line follows the agent's last answer.
                    TellAgentFarewell(originalSender, agentName);

                    // The farewell hands the conversation back to Morgana, so the departed agent's row leaves active duty in the same step.
                    // Awaited before the next message is read: a user returning at once to the same agent cannot have the new turn's active row switched off behind it.
                    if (currentIntent is { Length: > 0 })
                        await conversationPersistenceService.DeactivateAgentAsync(conversationId, currentIntent);
                }

                // The turn ends, counted under the active agent's intent and its completion.
                CloseTurnSpan(intent: currentIntent, completed: response.IsCompleted);

                // The active agent, not the state, carries a follow-up forward.
                Become(Idle);
            }
            catch (Exception ex)
            {
                actorLogger.Error(ex, "Error processing follow-up AgentResponse");

                // An answer that could not be handled leaves no agent to keep talking to: the next message is classified afresh.
                activeAgent = null;
                activeAgentIntent = null;

                // The user receives an apology in place of the answer.
                originalSender.Tell(new Records.ConversationResponse(
                    "I apologize, the runes are misaligned. Cast your intent once more.",
                    null,
                    null,
                    Constants.Morgana,
                    false,
                    null,
                    DateTime.UtcNow,
                    null));

                // The turn ends as errored, carrying the exception.
                CloseTurnSpan(ActivityStatusCode.Error, ex.Message, intent: currentIntent, completed: false, exception: ex);

                // The next message starts a new turn.
                Become(Idle);
            }
        });

        // The provider refused the active agent's own request on content grounds.
        ReceiveAsync<Records.ContentFilterRejection>(_ => HandleContentFilterRejectionAsync(originalSender));

        RegisterCommonHandlers();
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Closes the turn span and records the turn's duration and count, wherever a turn ends.
    /// A second call finds no span and does nothing, which is what lets PostStop close whatever turn is still open.
    /// </summary>
    private void CloseTurnSpan(
        ActivityStatusCode status = ActivityStatusCode.Ok,
        string? description = null,
        string? intent = null,
        bool? completed = null,
        Exception? exception = null)
    {
        // No turn is open: the turn has already closed or never started.
        if (turnSpan is null)
            return;

        // An errored turn carries its cause; a normal one keeps the span's default status.
        if (status == ActivityStatusCode.Error)
        {
            turnSpan.SetStatus(status, description);
            if (exception is not null)
                turnSpan.AddException(exception);
        }

        // The turn lasted from its span's start until now, the span still being open.
        Telemetry.TurnDuration.Record((DateTime.UtcNow - turnSpan.StartTimeUtc).TotalMilliseconds);

        // The turn is counted by intent and completion, so a dashboard can break the volume down by either.
        Telemetry.TurnCounter.Add(1,
            new KeyValuePair<string, object?>("intent", intent ?? "unknown"),
            new KeyValuePair<string, object?>("completed", completed ?? false));

        // Ends the span and forgets it so no later path closes it twice.
        turnSpan.Dispose();
        turnSpan = null;
    }

    /// <summary>Opens the cancellation scope of the phase that is about to start and returns its token.</summary>
    private CancellationToken StartPhaseCancellation()
    {
        // The previous phase has ended by the time a new one starts, so its source is released.
        phaseCancellation?.Dispose();

        // The new source is what a timeout of this phase cancels.
        phaseCancellation = new CancellationTokenSource();

        // The token travels with the request to whoever does the work of the phase.
        return phaseCancellation.Token;
    }

    /// <summary>Offers the user the colliding intents as buttons, in place of routing the message to an agent.</summary>
    private async Task SendDisambiguationAsync(Records.ProcessingContext ctx, string collidingIntentNames)
    {
        // The classifier lists the colliding intents most confident first.
        string[] intentNames = collidingIntentNames.Split(',', StringSplitOptions.RemoveEmptyEntries);

        actorLogger.Info($"Classification ambiguous, offering disambiguation among [{collidingIntentNames}]");

        // A button needs the intent's label and sample phrase, which only its definition carries.
        List<Records.IntentDefinition> configuredIntents = await agentConfigService.GetIntentsAsync();
        Dictionary<string, Records.IntentDefinition> intentsByName =
            configuredIntents.ToDictionary(intent => intent.Name, StringComparer.OrdinalIgnoreCase);

        // One button per colliding intent, most confident first: a press sends the intent's own sample phrase
        // and lands on that intent's agent. A name that no configured intent carries offers no button.
        List<QuickReply> quickReplies =
        [
            .. intentNames
                .Where(intentsByName.ContainsKey)
                .Select(name =>
                {
                    Records.IntentDefinition intent = intentsByName[name];
                    return new QuickReply(
                        intent.Name,
                        intent.Label,
                        intent.DefaultValue);
                })
        ];

        // The question that introduces the buttons is worded in the Classifier prompt.
        Records.Prompt classifierPrompt = await promptResolverService.ResolveAsync(Constants.Prompts.Classifier);
        string disambiguationMessage = classifierPrompt.GetAdditionalProperty<string>(Constants.PromptProperties.DisambiguationMessage);

        // Morgana asks the question herself and waits on the user with no agent behind her:
        // a press lands on its agent, any other message goes through the guard and the classifier again.
        ctx.OriginalSender.Tell(new Records.ConversationResponse(
            disambiguationMessage,
            ctx.Classification?.Intent,
            ctx.Classification?.Metadata,
            Constants.Morgana,
            false,
            quickReplies,
            null,
            null));

        // The turn ends uncompleted, counted under the top intent.
        CloseTurnSpan(intent: ctx.Classification?.Intent, completed: false);

        // The next message starts a new turn.
        Become(Idle);
    }

    /// <summary>Ends the turn on the provider's refusal of an agent's request, as a guard rejection would.</summary>
    private Task HandleContentFilterRejectionAsync(IActorRef originalSender)
    {
        // Both agent states arm a window and the refusal can arrive in either of them.
        Context.SetReceiveTimeout(null);

        actorLogger.Warning("Content filter rejection received from agent, treating as guard rejection");

        // The refusal is the whole reply, spoken by the active agent when there is one and by Morgana otherwise.
        originalSender.Tell(new Records.ConversationResponse(
            "Content policy violation",
            activeAgentIntent,
            null,
            activeAgentIntent != null ? GetAgentDisplayName(activeAgentIntent) : Constants.Morgana,
            false,
            null,
            DateTime.UtcNow,
            null));

        // A refusal on content grounds counts as a guard rejection.
        Telemetry.GuardRejectionCounter.Add(1);

        // The provider's shield fired on an agent's own request, which a poisoned tool result can provoke.
        TagList securityTags = new TagList
        {
            { Telemetry.SecurityDoor, Telemetry.SecurityDoors.Agent },
            { Telemetry.SecurityOutcome, Telemetry.SecurityOutcomes.Rejected },
            { Telemetry.SecuritySource, nameof(Records.GuardVerdictSource.ProviderFilter) }
        };

        // The agent is named only when one was active.
        if (activeAgentIntent != null)
            securityTags.Add(Telemetry.AgentIntent, activeAgentIntent);

        // Counts the rejection at the agent's door.
        Telemetry.SecurityEvents.Add(1, securityTags);

        // The turn ends uncompleted, counted under the active agent's intent.
        CloseTurnSpan(intent: activeAgentIntent, completed: false);

        // The next message starts a new turn.
        Become(Idle);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Says Morgana's closing line for a departing agent, right behind that agent's last answer.
    /// Said here it is hers, dated when it was said and the same in every channel.
    /// </summary>
    private void TellAgentFarewell(IActorRef sender, string departingAgentName)
    {
        // Only a specialised agent earns a farewell: Morgana finishing a turn of her own is just a turn.
        if (string.Equals(departingAgentName, Constants.Morgana, StringComparison.OrdinalIgnoreCase))
            return;

        // The closing line is worded in the Morgana prompt, with the departing agent's name as its placeholder.
        string farewellTemplate = promptResolverService
            .ResolveAsync(Constants.Morgana).GetAwaiter().GetResult()
            .GetAdditionalProperty<string>(Constants.PromptProperties.AgentExitMessage);

        // A configuration without the line says nothing.
        if (string.IsNullOrWhiteSpace(farewellTemplate))
            return;

        // Undated on purpose: nobody recorded it, so it is filed as Morgana's and stamped after
        // the answer it follows.
        sender.Tell(new Records.ConversationResponse(
            string.Format(CultureInfo.InvariantCulture, farewellTemplate, departingAgentName),
            null,
            null,
            Constants.Morgana,
            true,
            null,
            null,
            null));
    }

    /// <summary>Names the speaker of an intent for the client: the bare persona or the persona qualified by the intent.</summary>
    private string GetAgentDisplayName(string? intent)
    {
        // No intent or the catch-all Intents.Other: shown as the bare persona, with no agent name.
        if (string.IsNullOrEmpty(intent) || string.Equals(intent, Constants.Intents.Other, StringComparison.OrdinalIgnoreCase))
            return Constants.Morgana;

        // Otherwise capitalizes the intent for display and qualifies the persona with it.
        string capitalizedIntent = char.ToUpperInvariant(intent[0]) + intent[1..];
        return $"Morgana ({capitalizedIntent})";
    }

    /// <summary>
    /// Takes back the agent the conversation was left talking to, as its record states it, through the router.
    /// When nothing is active on record or the router cannot bring that agent up, the next message is classified as a new request.
    /// </summary>
    private async Task RestorePersistedActiveAgentAsync()
    {
        // The record names at most one active agent per conversation.
        string? persistedAgentIntent = await conversationPersistenceService.GetMostRecentActiveAgentAsync(conversationId);

        // No agent was left mid-exchange, so Morgana holds the conversation.
        if (persistedAgentIntent is null)
            return;

        actorLogger.Info($"Restoring active agent: {persistedAgentIntent}");

        try
        {
            // The turn about to start routes by this answer, so it is had before the turn goes on.
            // A one-off lookup: nothing streams and the router asks nothing back of this supervisor.
            Records.RestoreAgentResponse restoreResponse = await router.Ask<Records.RestoreAgentResponse>(
                new Records.RestoreAgentRequest(persistedAgentIntent),
                ActorSystemTimeout);

            // The agent the router brought up becomes the active one; a router that found none leaves Morgana holding the conversation.
            activeAgent = restoreResponse.AgentRef;
            activeAgentIntent = restoreResponse.AgentRef is null ? null : restoreResponse.AgentIntent;
        }
        catch (Exception ex)
        {
            // A router that failed or stayed silent leaves Morgana holding the conversation.
            actorLogger.Error(ex, $"Router did not bring up agent '{persistedAgentIntent}'");
        }

        if (activeAgent is null)
            actorLogger.Warning($"Could not restore agent for intent '{persistedAgentIntent}' - no active agent");
        else
            actorLogger.Info($"Active agent restored: {activeAgent.Path} with intent {activeAgentIntent}");
    }

    /// <summary>
    /// Stops the work still running for the conversation, the pipeline's actors and the spans still open.
    /// The guard, the classifier and the router live flat under the guardian, so nothing else would stop them.
    /// </summary>
    protected override void PostStop()
    {
        // A conversation that ends mid-turn stops the work still running for it.
        phaseCancellation?.Cancel();
        phaseCancellation?.Dispose();

        // The guard span is still open when the actor stopped before the verdict.
        if (guardSpan is not null)
        {
            guardSpan.SetStatus(ActivityStatusCode.Error, "actor stopped mid-turn");
            guardSpan.Dispose();
            guardSpan = null;
        }

        // The classifier span is still open when the actor stopped before the classification.
        if (classifierSpan is not null)
        {
            classifierSpan.SetStatus(ActivityStatusCode.Error, "actor stopped mid-turn");
            classifierSpan.Dispose();
            classifierSpan = null;
        }

        // The turn span closes after its children, as errored.
        CloseTurnSpan(ActivityStatusCode.Error, "actor stopped mid-turn", intent: activeAgentIntent, completed: false);

        // The pipeline of this conversation goes with its supervisor: the next message brings up a new one.
        Context.Stop(guard);
        Context.Stop(classifier);
        Context.Stop(router);

        actorLogger.Info($"ConversationSupervisorActor stopped for {conversationId}");

        base.PostStop();
    }

    #endregion
}