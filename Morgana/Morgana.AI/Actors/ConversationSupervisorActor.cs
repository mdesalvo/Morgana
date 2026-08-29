using System.Diagnostics;
using Akka.Actor;
using Akka.Event;
using Microsoft.Extensions.Configuration;
using Morgana.AI.Abstractions;
using Morgana.AI.Extensions;
using Morgana.AI.Interfaces;
using Morgana.AI.Telemetry;
using Morgana.Contracts;
using Status = Akka.Actor.Status;

namespace Morgana.AI.Actors;

/// <summary>
/// Main FSM orchestration actor: supervises conversation flow through guard check, classification,
/// agent routing, and follow-up handling. Tracks active agent for multi-turn conversations.
/// 5 states: Idle, AwaitingGuardCheck, AwaitingClassification, AwaitingAgentResponse,
/// AwaitingFollowUpResponse. Manages OpenTelemetry context hierarchy via TurnContext.
/// </summary>
public class ConversationSupervisorActor : MorganaActor
{
    private readonly IChannelService channelService;
    private readonly IChannelMetadataStore channelMetadataStore;
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
    private Activity? turnSpan;

    /// <summary>OTel span covering the guard-check duration (opened before Tell, closed on response).</summary>
    private Activity? guardSpan;

    /// <summary>OTel span covering the classification duration (opened before Tell, closed on response).</summary>
    private Activity? classifierSpan;

    /// <summary>
    /// Initializes a new instance of the ConversationSupervisorActor.
    /// Creates child actors (guard, classifier, router) and enters Idle state.
    /// </summary>
    public ConversationSupervisorActor(
        string conversationId,
        ILLMService llmService,
        IPromptResolverService promptResolverService,
        IChannelService channelService,
        IChannelMetadataStore channelMetadataStore,
        IAgentConfigurationService agentConfigService,
        IPresenterService presenterService,
        IConfiguration configuration) : base(conversationId, llmService, promptResolverService, configuration)
    {
        this.channelService = channelService;
        this.channelMetadataStore = channelMetadataStore;
        this.agentConfigService = agentConfigService;
        this.presenterService = presenterService;

        guard = Context.System.GetOrCreateActorAsync<GuardActor>(
            Constants.Actors.Guard, conversationId).GetAwaiter().GetResult();

        classifier = Context.System.GetOrCreateActorAsync<ClassifierActor>(
            Constants.Actors.Classifier, conversationId).GetAwaiter().GetResult();

        router = Context.System.GetOrCreateActorAsync<RouterActor>(
            Constants.Actors.Router, conversationId).GetAwaiter().GetResult();

        // Supervisor always starts in Idle state
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

        // No receive timeout while idle: this is the ONLY state that's meant to sit and wait
        // indefinitely (for the next user message). Every other state sets a timeout because it's
        // waiting on a specific in-flight operation (guard/classifier/agent) that must not hang.
        Context.SetReceiveTimeout(null);

        // Generates the welcome message on conversation start (see HandlePresentationRequestAsync).
        ReceiveAsync<Records.GeneratePresentationMessage>(HandlePresentationRequestAsync);

        // Delivers the welcome message once generated (see HandlePresentationGenerated).
        ReceiveAsync<Records.PresentationContext>(HandlePresentationGenerated);

        // Starts a turn for an incoming user message (see HandleUserMessage).
        Receive<Records.UserMessage>(HandleUserMessage);

        RegisterCommonHandlers();
    }

    /// <summary>
    /// Opens the turn span and dispatches to <see cref="GuardActor"/>. Applies to both new
    /// requests and follow-ups: every user message goes through the guard check first.
    /// </summary>
    private void HandleUserMessage(Records.UserMessage msg)
    {
        IActorRef originalSender = Sender;

        actorLogger.Info("User message received, routing through guard check");

        // Starts "morgana.turn", the root OTel span for this entire turn — the one unit of work a
        // trace viewer (Jaeger/Tempo) shows per user message, with every later stage (guard,
        // classifier, router, agent) recorded as a child of it. It's linked (ActivityLink), not
        // parented, to the HTTP request span the controller propagated in msg.TurnContext: this
        // actor keeps processing after the HTTP response has already returned to the client, so a
        // parent/child pair (which a trace UI expects to close together) would be the wrong shape.
        ActivityLink[] links = msg.TurnContext != default ? [new ActivityLink(msg.TurnContext)] : [];
        turnSpan = MorganaTelemetry.Source.StartActivity(MorganaTelemetry.TurnActivity, ActivityKind.Internal, parentContext: default, links: links);
        turnSpan?.SetTag(MorganaTelemetry.ConversationId, conversationId);
        turnSpan?.SetTag(MorganaTelemetry.TurnUserMessage, msg.Text.Length > 200 ? msg.Text[..200] : msg.Text);
        ActivityContext turnContext = turnSpan?.Context ?? default;

        // Starts "morgana.guard", the first child span under morgana.turn — it exists so the guard
        // check's own latency and compliance verdict show up as their own row in a trace, separate
        // from classification/routing/agent time. Started here, before the Tell to GuardActor
        // rather than when its response arrives in AwaitingGuardCheck, so the recorded duration
        // covers the full round-trip — mailbox/dispatch latency included, not just GuardActor's
        // own processing time once it picks the message up.
        guardSpan = MorganaTelemetry.Source.StartActivity(MorganaTelemetry.GuardActivity, ActivityKind.Internal, turnContext);
        guardSpan?.SetTag(MorganaTelemetry.ConversationId, conversationId);

        // Moves the FSM to AwaitingGuardCheck, carrying ctx forward.
        Records.ProcessingContext ctx = new Records.ProcessingContext(msg, originalSender, TurnContext: turnContext);
        Become(() => AwaitingGuardCheck(ctx));

        // Engage the guard actor with the utterance from the user
        guard.Tell(new Records.GuardCheckRequest(msg.ConversationId, msg.Text));
    }

    /// <summary>
    /// Handles presentation generation requests.
    /// Loads displayable intents then delegates entirely to <see cref="IPresenterService"/>.
    /// </summary>
    private async Task HandlePresentationRequestAsync(Records.GeneratePresentationMessage _)
    {
        // ConversationManagerActor sends GeneratePresentationMessage on every fresh start,
        // but a resumed conversation whose actor was already alive (or a duplicate message
        // for any other reason) must not re-greet the user mid-conversation.
        if (hasPresented)
        {
            actorLogger.Info("Presentation already shown, skipping");
            return;
        }

        hasPresented = true;
        actorLogger.Info("Generating presentation message via IPresenterService");

        // GetIntentsAsync returns every configured intent, "other" and label-less ones included —
        // GetDisplayableIntents then strips those down to the subset a user can actually click on
        // a welcome-message quick reply (same filter LLMClassifierService's collision check now
        // also applies, for the same reason: "other" has no Label/DefaultValue to show as a button).
        List<Records.IntentDefinition> allIntents = await agentConfigService.GetIntentsAsync();
        Records.IntentCollection intentCollection = new Records.IntentCollection(allIntents);
        List<Records.IntentDefinition> displayableIntents = intentCollection.GetDisplayableIntents();

        // GenerateAsync does the actual work (LLM call or config fallback, see IPresenterService)
        // and never throws. The result is packaged into a PresentationContext and sent to Self
        // rather than handled inline here, so generating the copy (this method) and delivering it
        // over the channel (HandlePresentationGenerated, its own try/catch around the send) stay
        // two independent failure domains — a channel outage can't be confused with a generation bug.
        Records.PresentationResult result = await presenterService.GenerateAsync(displayableIntents, conversationId);
        Self.Tell(new Records.PresentationContext(result.Message, displayableIntents)
        {
            LLMQuickReplies = result.QuickReplies
        });
    }

    /// <summary>
    /// Handles the generated presentation and dispatches it to the client through
    /// <see cref="IChannelService"/>. The supervisor stays channel-agnostic.
    /// </summary>
    private async Task HandlePresentationGenerated(Records.PresentationContext ctx)
    {
        actorLogger.Info("Sending presentation to client via channel");

        // ctx.LLMQuickReplies is already List<QuickReply> (Morgana.Contracts) — this isn't a type
        // conversion, it's a defensive rebuild that only carries over Id/Label/Value and drops
        // whatever Termination the source item had (always false here in practice: the Presentation
        // prompt's JSON schema never asks the LLM for a termination flag, see morgana.json).
        List<QuickReply> quickReplies = ctx.LLMQuickReplies?
            .Select(qr => new QuickReply(qr.Id, qr.Label, qr.Value))
            .ToList() ?? [];

        try
        {
            // channelService is AdaptingChannelService (the IChannelService DI registration), not
            // a raw transport: this one call first runs the message through MorganaChannelAdapter
            // against the conversation's registered capabilities — a Rune-class channel can get
            // ctx.Message and quickReplies rewritten (rich card prose, buttons flattened to a
            // numbered list, markdown stripped) — before it's ever handed to the concrete SignalR
            // or webhook transport. Nothing below this line controls what the user actually sees.
            await channelService.SendMessageAsync(new ChannelMessage
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
            // Swallowed, not rethrown: a delivery failure here (e.g. a flaky webhook channel)
            // must not crash the supervisor or leave the conversation stuck — the client simply
            // sees no welcome message and can still send its first message normally.
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

        // Bounds how long the round-trip to GuardActor may take before the ReceiveTimeout
        // handler below fires and FailOpen takes over — the same shared per-phase budget every
        // other Awaiting* state arms with its own call, not a guard-specific allowance.
        Context.SetReceiveTimeout(TimeSpan.FromSeconds(
            Convert.ToInt32(configuration["Morgana:ActorSystem:TimeoutSeconds"])));

        // The compliant-or-not verdict actually arriving from GuardActor — the two handlers
        // below (Status.Failure, ReceiveTimeout) cover the other ways this round-trip can end:
        // GuardActor throwing, or it simply never answering in time.
        Receive<Records.GuardCheckResponse>(response => {
            // Cancels the guard-check window now that GuardActor actually answered — this
            // handler is about to Become() into AwaitingClassification, AwaitingFollowUpResponse,
            // or Idle on rejection, and each of those arms (or clears) its own timeout
            // independently. Clearing here just guarantees the guard check's own window never
            // carries over into whatever state runs next.
            Context.SetReceiveTimeout(null);

            // Close and dispose the guard span by tracking compliance and the eventually reported violation
            guardSpan?.SetTag(MorganaTelemetry.GuardCompliant, response.Compliant);
            if (!response.Compliant && response.Violation != null)
                guardSpan?.SetTag(MorganaTelemetry.GuardViolation, response.Violation);
            if (guardSpan is not null)
                MorganaTelemetry.GuardDuration.Record((DateTime.UtcNow - guardSpan.StartTimeUtc).TotalMilliseconds);
            guardSpan?.Dispose();
            guardSpan = null;

            // A rejection short-circuits the whole turn right here — no classification, no
            // routing, no agent — exactly like SendDisambiguationAsync's early return further
            // down: the client gets an answer and the turn ends, with the next user message
            // re-entering guard check fresh (activeAgent, if any, is left untouched either way).
            if (!response.Compliant)
            {
                actorLogger.Warning($"Message rejected by guard: {response.Violation}");

                // Sends the guard's rejection text back to the client as the whole reply, tagged
                // with whatever classification is currently on ctx and the follow-up's active
                // agent name if there is one; no quick replies, timestamp, or rich card attached.
                ctx.OriginalSender.Tell(new Records.ConversationResponse(
                    response.Violation!,
                    ctx.Classification?.Intent,
                    ctx.Classification?.Metadata,
                    activeAgentIntent != null ? GetAgentDisplayName(activeAgentIntent) : "Morgana",
                    false,
                    null,
                    null,
                    null));

                // Increments the dedicated guard-rejection counter, kept separate from the
                // generic per-turn counter CloseTurnSpan emits below.
                MorganaTelemetry.GuardRejectionCounter.Add(1);

                // Closes the turn span, tagging it with whatever intent ctx.Classification currently carries.
                CloseTurnSpan(intent: ctx.Classification?.Intent, completed: false);

                // Returns to Idle without touching activeAgent or activeAgentIntent.
                Become(Idle);

                // Exits before reaching the compliant-path code below.
                return;
            }

            actorLogger.Info("Message passed guard check");

            // An active agent from a prior turn means this message is the next turn of an
            // ongoing exchange, not a new request to classify — classification is skipped
            // entirely and the message goes straight to that same agent instance.
            if (activeAgent != null)
            {
                actorLogger.Info($"Active agent exists, routing to follow-up flow with agent {activeAgent.Path}");

                // Moves the FSM to AwaitingFollowUpResponse, carrying the sender forward.
                Become(() => AwaitingFollowUpResponse(ctx.OriginalSender));

                // Passes no classification to the agent: this follow-up path doesn't classify.
                activeAgent.Tell(new Records.AgentRequest(
                    ctx.OriginalMessage.ConversationId,
                    ctx.OriginalMessage.Text,
                    null,
                    ctx.TurnContext,          // propagate context to agent
                    GetEffectiveCapabilities()));
            }
            else
            {
                actorLogger.Info("No active agent, proceeding to classification for new request");

                // Starts "morgana.classifier", the second child span under morgana.turn, before
                // the Tell to ClassifierActor — same rationale as the guard span opened in
                // HandleUserMessage: capturing the duration from here, not from when
                // AwaitingClassification receives the result, means it covers the full
                // round-trip rather than just ClassifierActor's own processing time.
                classifierSpan = MorganaTelemetry.Source.StartActivity(MorganaTelemetry.ClassifierActivity, ActivityKind.Internal, ctx.TurnContext);
                classifierSpan?.SetTag(MorganaTelemetry.ConversationId, conversationId);

                // Moves the FSM to AwaitingClassification, carrying ctx forward.
                Become(() => AwaitingClassification(ctx));

                // Engage the classifier with the utterance from the user (now that it passed guardrails)
                classifier.Tell(ctx.OriginalMessage);
            }
        });

        // Routes an explicit GuardActor failure (a thrown exception) to FailOpen.
        Receive<Status.Failure>(failure => FailOpen(failure.Cause.Message, failure.Cause));

        // Routes a stalled GuardActor (no response within the timeout above) to FailOpen.
        Receive<ReceiveTimeout>(_ => FailOpen("receive timeout", null));

        RegisterCommonHandlers();
        return;

        #region Locals
        // Shared fail-open path for both an explicit Status.Failure from GuardActor and
        // a ReceiveTimeout (guard service hung past the configured budget). Open, not closed:
        // an outage in moderation shouldn't be able to take down the whole product for everyone.
        void FailOpen(string description, Exception? cause)
        {
            // Turns off the timeout: GuardActor did respond, even though with a failure, so
            // there's no more reason to keep waiting. Whichever state this handler switches to
            // below (AwaitingFollowUpResponse or AwaitingClassification) will set its own timeout
            // when it starts.
            Context.SetReceiveTimeout(null);

            // Logs the failure itself and the "fail-open" decision
            if (cause != null)
                actorLogger.Error(cause, "Guard check failed: {0}", description);
            else
                actorLogger.Error("Guard check failed: {0}", description);
            actorLogger.Warning("Guard check failed, failing open (allowing message)");

            // Marks the guard span as errored and disposes it — same shape as the compliant
            // path's span, but tagged as a failure instead of a compliance verdict.
            guardSpan?.SetStatus(ActivityStatusCode.Error, description);
            if (cause != null)
                guardSpan?.AddException(cause);
            guardSpan?.Dispose();
            guardSpan = null;

            // Failing open doesn't change the routing decision: an active agent from a prior turn
            // still means this message continues that follow-up exchange, exactly like the
            // compliant path above — so it still goes straight to that agent, not to classification.
            if (activeAgent != null)
            {
                // Moves the FSM to AwaitingFollowUpResponse, carrying the sender forward.
                Become(() => AwaitingFollowUpResponse(ctx.OriginalSender));

                // Sends the follow-up request to the active agent. ctx.Classification is passed
                // here (instead of the literal null the compliant path above uses) but is still
                // unset either way, since this follow-up path never classifies.
                activeAgent.Tell(new Records.AgentRequest(
                    ctx.OriginalMessage.ConversationId,
                    ctx.OriginalMessage.Text,
                    ctx.Classification,
                    ctx.TurnContext,
                    GetEffectiveCapabilities()));
            }
            else
            {
                // No active agent: failing open doesn't change this either — it's still a fresh
                // request that needs classification, exactly like the compliant path above.
                // Opens the classifier span before dispatching to ClassifierActor, same reasoning
                // as the compliant path's classifierSpan above (its duration should cover the
                // full round-trip, not just ClassifierActor's own processing time).
                classifierSpan = MorganaTelemetry.Source.StartActivity(
                    MorganaTelemetry.ClassifierActivity,
                    ActivityKind.Internal,
                    ctx.TurnContext);
                classifierSpan?.SetTag(MorganaTelemetry.ConversationId, conversationId);

                // Moves the FSM to AwaitingClassification, carrying ctx forward.
                Become(() => AwaitingClassification(ctx));

                // Sends the message to ClassifierActor for classification.
                classifier.Tell(ctx.OriginalMessage);
            }
        }
        #endregion
    }

    /// <summary>
    /// AwaitingClassification state: waiting for intent classification result from ClassifierActor.
    /// Opens a morgana.classifier child span using the TurnContext from ProcessingContext.
    /// </summary>
    /// <param name="ctx">Processing context containing original message, sender, and OTel TurnContext</param>
    private void AwaitingClassification(Records.ProcessingContext ctx)
    {
        actorLogger.Info("→ State: AwaitingClassification");

        // Same shared per-phase budget as AwaitingGuardCheck's timeout, this time bounding the
        // round-trip to ClassifierActor before ReceiveTimeout below hands off to FallbackToOther.
        Context.SetReceiveTimeout(TimeSpan.FromSeconds(
            Convert.ToInt32(configuration["Morgana:ActorSystem:TimeoutSeconds"])));

        // The actual classification arriving from ClassifierActor — Status.Failure and
        // ReceiveTimeout below are the two ways it can fail to arrive at all, both routed
        // through the same FallbackToOther recovery.
        ReceiveAsync<Records.ClassificationResult>(async classification => {
            // Cancels the classification window now that ClassifierActor actually answered —
            // this handler is about to Become() into AwaitingAgentResponse (or reply directly and
            // Become(Idle) on a disambiguation), and either arms its own timeout independently.
            Context.SetReceiveTimeout(null);

            actorLogger.Info($"Classification result: {classification.Intent}");

            // Close and dispose the classifier span by tracking the top intent, its confidence and the full metadata
            classifierSpan?.SetTag(MorganaTelemetry.ClassificationIntent, classification.Intent);
            classifierSpan?.SetTag(MorganaTelemetry.ClassificationMetadata, classification.Metadata);
            if (classification.Metadata.TryGetValue("confidence", out string? confidence))
                classifierSpan?.SetTag(MorganaTelemetry.ClassificationConfidence, confidence);
            if (classifierSpan is not null)
                MorganaTelemetry.ClassifierDuration.Record((DateTime.UtcNow - classifierSpan.StartTimeUtc).TotalMilliseconds);
            classifierSpan?.Dispose();
            classifierSpan = null;

            // Builds a copy of the context with the classification attached, to carry forward into
            // AwaitingAgentResponse (or into the disambiguation reply below).
            Records.ProcessingContext updatedCtx = ctx with { Classification = classification };

            // A colliding classification (LLMClassifierService's confidence-gap check) is diverted
            // here instead of ever reaching the router: no agent is invoked, no active agent is set.
            if (classification.Metadata.TryGetValue("ambiguousIntents", out string? collidingIntentNames))
            {
                await SendDisambiguationAsync(updatedCtx, collidingIntentNames);
                return;
            }

            // Starts "morgana.router", the third child span under morgana.turn, before the Tell
            // to RouterActor — same reasoning as the guard and classifier spans above: opening it
            // here means its duration covers the full round-trip, not just the time RouterActor
            // itself takes to pick an agent. Unlike guardSpan/classifierSpan it's a local `using`,
            // not a field: nothing outside this method needs to close it from an async callback.
            using Activity? routerSpan = MorganaTelemetry.Source.StartActivity(
                MorganaTelemetry.RouterActivity,
                ActivityKind.Internal,
                ctx.TurnContext);
            routerSpan?.SetTag(MorganaTelemetry.RouterIntent, classification.Intent);

            // Moves the FSM to AwaitingAgentResponse, carrying the classified context forward.
            Become(() => AwaitingAgentResponse(updatedCtx));

            // Sends the request to RouterActor, which picks the agent for this intent.
            router.Tell(new Records.AgentRequest(
                ctx.OriginalMessage.ConversationId,
                ctx.OriginalMessage.Text,
                classification,
                ctx.TurnContext,              // propagate context to router → agent
                GetEffectiveCapabilities()));
        });

        // Routes an explicit ClassifierActor failure (a thrown exception) to FallbackToOther.
        Receive<Status.Failure>(failure => FallbackToOther(failure.Cause.Message, failure.Cause));

        // Routes a stalled ClassifierActor (no response within the timeout above) to FallbackToOther.
        Receive<ReceiveTimeout>(_ => FallbackToOther("receive timeout", null));

        RegisterCommonHandlers();
        return;
        
        #region Locals
        // Shared fallback-to-"other" path for both an explicit Status.Failure from ClassifierActor
        // and a ReceiveTimeout (classifier service hung past the configured budget).
        void FallbackToOther(string description, Exception? cause)
        {
            // Turns off the timeout: ClassifierActor did respond, even though with a failure, so
            // there's no more reason to keep waiting. The next state below, AwaitingAgentResponse,
            // will set its own timeout when it starts.
            Context.SetReceiveTimeout(null);

            // Logs the failure itself, with the exception attached when there is one.
            if (cause != null)
                actorLogger.Error(cause, "Classification failed: {0}", description);
            else
                actorLogger.Error("Classification failed: {0}", description);

            // Marks the classifier span as errored and disposes it — same shape as the success
            // path's span close above, tagged as a failure instead of a classification result.
            classifierSpan?.SetStatus(ActivityStatusCode.Error, description);
            if (cause != null)
                classifierSpan?.AddException(cause);
            classifierSpan?.Dispose();
            classifierSpan = null;

            // Shaped exactly like a real ClassifierActor result — same "confidence" key, same
            // string type — so nothing downstream (the confidence tag read a few lines above in
            // the success path, or any future consumer) needs a special case for the failure
            // path; "error" is the one extra key a successful classification never carries, kept
            // here purely for diagnostics.
            Records.ClassificationResult fallbackClassification = new Records.ClassificationResult(
                Constants.Intents.Other,
                new Dictionary<string, string>
                {
                    ["confidence"] = "0.00",
                    ["error"] = $"classification_failed: {description}"
                });

            // Routing to "other" still goes through the router below, even though "other" has no
            // registered agent by design (see HandlesIntentAgentRegistryService) — RouterActor
            // won't find one either and replies with its own unrecognized-intent fallback, which
            // AwaitingAgentResponse's bare Receive<AgentResponse> handler is exactly there to catch.
            actorLogger.Info("Falling back to 'other' intent");

            // Builds a copy of the context with the fallback classification attached, to carry forward
            // into AwaitingAgentResponse.
            Records.ProcessingContext updatedCtx = ctx with { Classification = fallbackClassification };

            // Same "morgana.router" span as the primary classification path above, opened here
            // for the same reason: its duration should cover the full round-trip to RouterActor.
            using Activity? routerSpan = MorganaTelemetry.Source.StartActivity(
                MorganaTelemetry.RouterActivity,
                ActivityKind.Internal,
                ctx.TurnContext);
            routerSpan?.SetTag(MorganaTelemetry.RouterIntent, fallbackClassification.Intent);

            // Moves the FSM to AwaitingAgentResponse, carrying the fallback-classified context forward.
            Become(() => AwaitingAgentResponse(updatedCtx));

            // Sends the request to RouterActor, same as the primary classification path above —
            // it will find no agent for "other" and reply with its own unrecognized-intent fallback.
            router.Tell(new Records.AgentRequest(
                ctx.OriginalMessage.ConversationId,
                ctx.OriginalMessage.Text,
                fallbackClassification,
                ctx.TurnContext,
                GetEffectiveCapabilities()));
        }
        #endregion
    }

    /// <summary>
    /// AwaitingAgentResponse state: waiting for specialized agent to process the request.
    /// Annotates the turn span with agent name and intent on response.
    /// </summary>
    /// <param name="ctx">Processing context containing original message, sender, and classification</param>
    private void AwaitingAgentResponse(Records.ProcessingContext ctx)
    {
        actorLogger.Info("→ State: AwaitingAgentResponse");

        // Same shared per-phase budget again, now bounding the round-trip through RouterActor to
        // whichever domain agent it dispatches to — re-armed on every AgentStreamChunk below, so
        // it only fires if the agent goes fully silent for a whole window, not merely slow.
        Context.SetReceiveTimeout(TimeSpan.FromSeconds(Convert.ToInt32(configuration["Morgana:ActorSystem:TimeoutSeconds"])));

        // Fires if neither RouterActor, nor the domain agent it dispatches to, answer in time.
        Receive<ReceiveTimeout>(_ =>
        {
            actorLogger.Error($"Timeout waiting for agent response (classification: {ctx.Classification?.Intent})");

            // Turns off the timeout now that it has fired, so it doesn't fire again for whatever runs next.
            Context.SetReceiveTimeout(null);

            // Sends a generic apology back to the client. Nothing touches activeAgent here: this
            // timeout fires before RouterActor has ever confirmed an agent for this turn, so there
            // is no active agent yet to drop — contrast the identical-looking timeout in
            // AwaitingFollowUpResponse below, which does clear one because it was already set.
            ctx.OriginalSender.Tell(new Records.ConversationResponse(
                "I apologize, time ran out before the cauldron could brew your answer. Cast it again.",
                ctx.Classification?.Intent,
                ctx.Classification?.Metadata,
                GetAgentDisplayName(ctx.Classification?.Intent),
                false,
                null,
                ctx.OriginalMessage.Timestamp,
                null));

            // Closes the turn span with an error status, tagged with whatever intent was classified for this turn.
            CloseTurnSpan(ActivityStatusCode.Error, "Timeout waiting for agent response", intent: ctx.Classification?.Intent, completed: false);

            // Returns to Idle.
            Become(Idle);
        });

        // Forwards a streamed partial response straight to the client as it arrives.
        Receive<Records.AgentStreamChunk>(chunk =>
        {
            // Re-arms the timeout on every chunk, not just once at state entry: a long response
            // streamed token-by-token keeps resetting its own deadline as long as it keeps
            // producing output, so only a genuinely stalled agent (no chunk, no final response,
            // for a full timeout window) trips ReceiveTimeout below — a slow-but-alive stream never does.
            Context.SetReceiveTimeout(TimeSpan.FromSeconds(Convert.ToInt32(configuration["Morgana:ActorSystem:TimeoutSeconds"])));
            ctx.OriginalSender.Tell(chunk);
        });

        // ActiveAgentResponse comes from RouterActor when it found and ran a real agent for the
        // classified intent; AgentResponse below is the OTHER possible reply, RouterActor's own
        // fallback when no agent handles that intent at all (see UnrecognizedIntentError).
        Receive<Records.ActiveAgentResponse>(response =>
        {
            // Cancels the routing/agent window now that a real agent actually answered — this
            // handler is about to Become(Idle), which arms no timeout of its own.
            Context.SetReceiveTimeout(null);

            try
            {
                // Resolves the display name for the classified agent
                string agentName = GetAgentDisplayName(ctx.Classification?.Intent);

                actorLogger.Info($"Received ActiveAgentResponse from {response.AgentRef.Path}, " +
                                 $"completed: {response.IsCompleted}, " +
                                 $"quickReplies: {response.QuickReplies?.Count ?? 0}");

                // The conversation with the user is flagged as "completed" by LLM:
                // the active agent and its intent are cleared
                if (response.IsCompleted)
                {
                    actorLogger.Info("Agent signaled completion, clearing active agent");
                    activeAgent = null;
                    activeAgentIntent = null;
                }
                // The conversation with the user is still ongoing:
                // the active agent and its intent are restated
                else
                {
                    actorLogger.Info($"Agent signaled incomplete, setting as active agent: {response.AgentRef.Path}");
                    activeAgent = response.AgentRef;
                    activeAgentIntent = ctx.Classification?.Intent;
                }

                // Sends the agent's response back to the client, forwarding the classification's
                // intent and metadata, the agent's completion flag, quick replies and rich card,
                // and the original message's timestamp.
                ctx.OriginalSender.Tell(new Records.ConversationResponse(
                    response.Response,
                    ctx.Classification?.Intent,
                    ctx.Classification?.Metadata,
                    agentName,
                    response.IsCompleted,
                    response.QuickReplies,
                    ctx.OriginalMessage.Timestamp,
                    response.RichCard));

                // Closes the turn span, tagged with the classified intent and whether the agent completed.
                CloseTurnSpan(intent: ctx.Classification?.Intent, completed: response.IsCompleted);

                // Returns to Idle either way — activeAgent above, not the FSM state, carries any follow-up forward.
                Become(Idle);
            }
            catch (Exception ex)
            {
                actorLogger.Error(ex, "Error processing ActiveAgentResponse");

                // Clears the active agent, unlike a guard/content-filter rejection which leaves
                // it in place — the next message starts a fresh classify-then-route turn.
                activeAgent = null;
                activeAgentIntent = null;

                // Sends a generic apology back to the client, since the agent's actual response couldn't be processed.
                ctx.OriginalSender.Tell(new Records.ConversationResponse(
                    "I apologize, the potion bubbled over in error. Repeat your incantation.",
                    ctx.Classification?.Intent,
                    ctx.Classification?.Metadata,
                    GetAgentDisplayName(ctx.Classification?.Intent),
                    false,
                    null,
                    ctx.OriginalMessage.Timestamp,
                    null));

                // Closes the turn span with an error status, attaching the exception and tagging
                // it with whatever intent was classified for this turn.
                CloseTurnSpan(ActivityStatusCode.Error, ex.Message, intent: ctx.Classification?.Intent, completed: false, exception: ex);

                // Returns to Idle.
                Become(Idle);
            }
        });

        // AgentResponse is RouterActor's own fallback reply, sent when no agent handles the
        // classified intent at all — see the comment above ActiveAgentResponse.
        Receive<Records.AgentResponse>(response =>
        {
            // Cancels the same routing/agent window as ActiveAgentResponse above — RouterActor's
            // fallback still answered in time, and this handler also Become(Idle)s next.
            Context.SetReceiveTimeout(null);

            try
            {
                actorLogger.Info("Received fallback response from router (no specialized agent)");

                // Sends the router's fallback text back to the client. AgentCompleted is
                // hardcoded to true (not response.IsCompleted): there's no agent behind this
                // reply to possibly continue with, so the turn is over by construction. AgentName
                // is hardcoded to "Morgana" rather than resolved via GetAgentDisplayName, since no
                // specific agent handled this intent.
                ctx.OriginalSender.Tell(new Records.ConversationResponse(
                    response.Response,
                    ctx.Classification?.Intent,
                    ctx.Classification?.Metadata,
                    "Morgana",
                    true,
                    response.QuickReplies,
                    DateTime.UtcNow,
                    response.RichCard));

                // Closes the turn span as completed, tagged with the classified intent.
                CloseTurnSpan(intent: ctx.Classification?.Intent, completed: true);

                // Returns to Idle.
                Become(Idle);
            }
            catch (Exception ex)
            {
                actorLogger.Error(ex, "Error processing fallback AgentResponse");

                // Sends a generic apology back to the client, since the router's fallback text above couldn't be processed.
                ctx.OriginalSender.Tell(new Records.ConversationResponse(
                    "I apologize, the grimoire slammed shut. Utter the words once more.",
                    ctx.Classification?.Intent,
                    ctx.Classification?.Metadata,
                    "Morgana",
                    false,
                    null,
                    DateTime.UtcNow,
                    null));

                // Closes the turn span with an error status, attaching the exception and tagging
                // it with whatever intent was classified for this turn.
                CloseTurnSpan(ActivityStatusCode.Error, ex.Message, intent: ctx.Classification?.Intent, completed: false, exception: ex);

                // Returns to Idle.
                Become(Idle);
            }
        });

        // Handles a content-policy violation the agent itself flagged mid-turn (see HandleContentFilterRejectionAsync).
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

        // Same shared per-phase budget once more, now bounding the round-trip to the already-active
        // agent — re-armed on every AgentStreamChunk below, same reasoning as AwaitingAgentResponse.
        Context.SetReceiveTimeout(TimeSpan.FromSeconds(Convert.ToInt32(configuration["Morgana:ActorSystem:TimeoutSeconds"])));

        // Fires if the already-active agent doesn't answer this follow-up in time.
        Receive<ReceiveTimeout>(_ =>
        {
            actorLogger.Error($"Timeout waiting for follow-up response from active agent (intent: {activeAgentIntent})");

            // Turns off the timeout now that it has fired, so it doesn't fire again for whatever runs next.
            Context.SetReceiveTimeout(null);

            // Drops the active agent entirely, unlike AwaitingClassification's FallbackToOther
            // (no "other"-intent retry here) — the next message re-enters guard check with
            // activeAgent null and classifies as a brand-new request. timedOutIntent is saved
            // first so CloseTurnSpan below still has an intent to tag the span with.
            string? timedOutIntent = activeAgentIntent;
            activeAgent = null;
            activeAgentIntent = null;

            // Sends a generic apology back to the client — no intent or metadata to attach, since
            // this state never carries a ProcessingContext (see the constructor above).
            originalSender.Tell(new Records.ConversationResponse(
                "I apologize, the sands of time drained from the cauldron. Re-weave your spell.",
                null,
                null,
                "Morgana",
                false,
                null,
                DateTime.UtcNow,
                null));

            // Closes the turn span with an error status, tagged with the intent the dropped agent was handling.
            CloseTurnSpan(ActivityStatusCode.Error, "Timeout waiting for follow-up response", intent: timedOutIntent, completed: false);

            // Returns to Idle.
            Become(Idle);
        });

        // Forwards a streamed partial response straight to the client as it arrives. Re-arms the
        // timeout per chunk, same reason as AwaitingAgentResponse's identical handler above.
        Receive<Records.AgentStreamChunk>(chunk =>
        {
            // Re-arms the timeout, same reasoning as AwaitingAgentResponse's identical handler above.
            Context.SetReceiveTimeout(TimeSpan.FromSeconds(Convert.ToInt32(configuration["Morgana:ActorSystem:TimeoutSeconds"])));

            // Passes the chunk through unchanged to the client that sent the follow-up message.
            originalSender.Tell(chunk);
        });

        // Handles the already-active agent's reply to this follow-up message.
        Receive<Records.AgentResponse>(response =>
        {
            // Cancels the follow-up window now that the active agent actually answered — this
            // handler always Become(Idle)s next, completed or not: multi-turn stickiness lives
            // entirely in the activeAgent field (left set below when IsCompleted is false), not
            // in staying parked in this FSM state.
            Context.SetReceiveTimeout(null);

            // Captures the intent before it can be cleared below (IsCompleted clears activeAgentIntent),
            // so CloseTurnSpan still has something to tag the span with, whichever branch runs.
            string? currentIntent = activeAgentIntent;
            try
            {
                string agentName = currentIntent != null ? GetAgentDisplayName(currentIntent) : "Morgana";

                // Clears the active agent only once it's actually done: this is the same agent
                // that was already active replying again, so while it keeps working
                // (IsCompleted=false) it simply remains the active agent, unchanged.
                if (response.IsCompleted)
                {
                    actorLogger.Info("Active agent signaled completion, clearing active agent");
                    activeAgent = null;
                    activeAgentIntent = null;
                }

                // Sends the agent's response back to the client. Unlike AwaitingAgentResponse's
                // equivalent Tell, Intent/Metadata are hardcoded null and the timestamp is
                // DateTime.UtcNow rather than an original message timestamp — this state has
                // neither to forward (see the comment above currentIntent).
                originalSender.Tell(new Records.ConversationResponse(
                    response.Response,
                    null,
                    null,
                    agentName,
                    response.IsCompleted,
                    response.QuickReplies,
                    DateTime.UtcNow,
                    response.RichCard));

                // Closes the turn span, tagged with the active agent's intent and whether it completed.
                CloseTurnSpan(intent: currentIntent, completed: response.IsCompleted);

                // Returns to Idle either way — activeAgent above, not the FSM state, carries any follow-up forward.
                Become(Idle);
            }
            catch (Exception ex)
            {
                actorLogger.Error(ex, "Error processing follow-up AgentResponse");

                // Clears the active agent: whatever broke here happened while handling its
                // response, so there's no known-good state left to keep talking to.
                activeAgent = null;
                activeAgentIntent = null;

                // Sends a generic apology back to the client, since the agent's actual response
                // above couldn't be processed.
                originalSender.Tell(new Records.ConversationResponse(
                    "I apologize, the runes are misaligned. Cast your intent once more.",
                    null,
                    null,
                    "Morgana",
                    false,
                    null,
                    DateTime.UtcNow,
                    null));

                // Closes the turn span with an error status, attaching the exception and tagging
                // it with the intent the active agent was handling.
                CloseTurnSpan(ActivityStatusCode.Error, ex.Message, intent: currentIntent, completed: false, exception: ex);

                // Returns to Idle.
                Become(Idle);
            }
        });

        // Handles a content-policy violation the active agent itself flagged mid-turn (see HandleContentFilterRejectionAsync).
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
        // Guards against turnSpan already being null: PostStop, or a second call reaching here
        // after the turn already closed once, must be safe no-ops.
        if (turnSpan is not null)
        {
            // Marks the span as failed and attaches the exception, but only for an error close —
            // a normal completion leaves the span's default Ok status untouched.
            if (status == ActivityStatusCode.Error)
            {
                turnSpan.SetStatus(status, description);
                if (exception is not null)
                    turnSpan.AddException(exception);
            }

            // Note: Activity.Duration is only populated after Stop()/Dispose(), which happens a
            // few lines below this — so at this point turnSpan.Duration is always still Zero and
            // the branch below always runs. Left here as the correct fallback in case this method
            // is ever called after the span has already been stopped elsewhere.
            double durationMs = (turnSpan.Duration != TimeSpan.Zero)
                ? turnSpan.Duration.TotalMilliseconds
                : (DateTime.UtcNow - turnSpan.StartTimeUtc).TotalMilliseconds;

            // Records the turn's duration and increments the per-turn counter, both tagged by
            // intent and completion so a dashboard can break volume and latency down by either.
            MorganaTelemetry.TurnDuration.Record(durationMs);
            MorganaTelemetry.TurnCounter.Add(1,
                new KeyValuePair<string, object?>("intent", intent ?? "unknown"),
                new KeyValuePair<string, object?>("completed", completed ?? false));

            turnSpan.Dispose();
            turnSpan = null;
        }
    }

    /// <summary>
    /// Sends a disambiguation quick-reply straight to the client instead of routing to an agent.
    /// Called when <see cref="Services.LLMClassifierService"/>'s confidence-gap check flags a
    /// collision — see <see cref="Records.ClassificationResult"/> for the metadata contract.
    /// </summary>
    private async Task SendDisambiguationAsync(Records.ProcessingContext ctx, string collidingIntentNames)
    {
        // Get the list of colliding intents from the response of classifier
        string[] intentNames = collidingIntentNames.Split(',', StringSplitOptions.RemoveEmptyEntries);

        actorLogger.Info($"Classification ambiguous, offering disambiguation among [{collidingIntentNames}]");

        // We need the full IntentDefinition (Label + DefaultValue) for each colliding name, not just
        // the bare name the classifier gave us — that's what turns a plain intent identifier like
        // "billing" into a clickable button with a friendly label and a ready-to-send sample phrase.
        List<Records.IntentDefinition> allIntents = await agentConfigService.GetIntentsAsync();
        Dictionary<string, Records.IntentDefinition> intentsByName =
            allIntents.ToDictionary(intent => intent.Name, StringComparer.OrdinalIgnoreCase);

        // One QuickReply per colliding intent, most-confident first. Value is the intent's own
        // DefaultValue sample phrase (same fallback the Presenter uses) — tapping the button
        // resubmits that phrase as the user's next message, which classifies unambiguously.
        List<QuickReply> quickReplies =
        [
            .. intentNames
                .Where(intentsByName.ContainsKey)
                .Select(name =>
                {
                    Records.IntentDefinition intent = intentsByName[name];
                    return new QuickReply(
                        intent.Name,
                        intent.Label ?? intent.Name,
                        intent.DefaultValue ?? $"Help me with {intent.Name}");
                })
        ];

        // Get the disambiguation message from the classifier's prompt
        Records.Prompt classifierPrompt = await promptResolverService.ResolveAsync(Constants.Prompts.Classifier);
        string disambiguationMessage = classifierPrompt.GetAdditionalProperty<string>("DisambiguationMessage");

        // Tell the response straight to the client — no router, no agent, exactly like a Guard
        // rejection or the Presentation message. AgentCompleted:false signals "I'm not done, I'm
        // waiting on you" even though there is no activeAgent behind it: the next message from the
        // user is just a normal fresh turn that re-enters guard check → classification from scratch.
        ctx.OriginalSender.Tell(new Records.ConversationResponse(
            disambiguationMessage,
            ctx.Classification?.Intent,
            ctx.Classification?.Metadata,
            "Morgana",
            false,
            quickReplies,
            ctx.OriginalMessage.Timestamp,
            null));

        // Closes the turn span, tagged with the colliding intent the classifier reported.
        CloseTurnSpan(intent: ctx.Classification?.Intent, completed: false);

        // Returns to Idle.
        Become(Idle);
    }

    /// <summary>
    /// Handles a content filter rejection from an agent as if it were a guard rejection.
    /// Uses the same rejection shape and increments the guard rejection counter.
    /// </summary>
    private Task HandleContentFilterRejectionAsync(IActorRef originalSender)
    {
        // Cancels whatever timeout was active: this is wired into every Awaiting* state, all of
        // which arm one, and a content-filter rejection can arrive from any of them.
        Context.SetReceiveTimeout(null);

        actorLogger.Warning("Content filter rejection received from agent, treating as guard rejection");

        // Sends the same shape as a guard rejection: no metadata, no quick replies, AgentName
        // resolved from the active agent if this happened mid-follow-up, "Morgana" otherwise.
        originalSender.Tell(new Records.ConversationResponse(
            "Content policy violation",
            activeAgentIntent,
            null,
            activeAgentIntent != null ? GetAgentDisplayName(activeAgentIntent) : "Morgana",
            false,
            null,
            DateTime.UtcNow,
            null));

        // Counts this the same as a guard rejection: a content-policy block either way.
        MorganaTelemetry.GuardRejectionCounter.Add(1);

        // Closes the turn span, tagged with whatever agent was active when the violation was flagged.
        CloseTurnSpan(intent: activeAgentIntent, completed: false);

        // Returns to Idle.
        Become(Idle);

        // No actual async work happens here — Task.CompletedTask just satisfies the ReceiveAsync
        // signature this handler is wired to.
        return Task.CompletedTask;
    }

    /// <summary>
    /// Resolves the capability budget to stamp on outgoing <see cref="Records.AgentRequest"/>s. A
    /// missing metadata entry is an invariant violation, not a recoverable case — the controller
    /// gate and ConversationManagerActor guarantee it exists before any turn reaches here.
    /// </summary>
    private ChannelCapabilities GetEffectiveCapabilities()
    {
        // Looks up the capabilities this conversation's channel declared at handshake time.
        // Missing means the invariant the doc comment above describes was violated — not a case
        // to recover from, hence the throw rather than a fallback default.
        if (!channelMetadataStore.TryGetChannelMetadata(conversationId, out ChannelMetadata? registeredChannelMetadata))
            throw new InvalidOperationException(
                $"No channel metadata registered for conversation {conversationId}; " +
                "the start-conversation gate and ConversationManagerActor should have ensured registration before any turn.");

        ChannelCapabilities channelCapabilities = registeredChannelMetadata.Capabilities;

        // True when the channel lacks at least one rich feature Morgana may produce — the case
        // MorganaChannelAdapter will need to rewrite the message for, downstream of this call.
        bool willNeedAdaptation = !channelCapabilities.SupportsRichCards
                                   || !channelCapabilities.SupportsQuickReplies
                                   || !channelCapabilities.SupportsMarkdown;

        // Streamed chunks bypass the adapter, so a channel that will need adaptation would show
        // the user raw content that then gets visibly rewritten once the adapted message lands —
        // suppressing streaming here avoids that jarring flash-then-rewrite.
        if (willNeedAdaptation && channelCapabilities.SupportsStreaming)
            return channelCapabilities with { SupportsStreaming = false };

        return channelCapabilities;
    }

    /// <summary>
    /// Builds the display name shown to the client for a given intent: the bare persona,
    /// or the persona qualified by the intent when one is available.
    /// </summary>
    private string GetAgentDisplayName(string? intent)
    {
        // No intent, or the catch-all "other": shown as the bare persona, with no agent name attached.
        if (string.IsNullOrEmpty(intent) || string.Equals(intent, Constants.Intents.Other, StringComparison.OrdinalIgnoreCase))
            return "Morgana";

        // Otherwise capitalizes the intent for display and qualifies the persona with it.
        string capitalizedIntent = char.ToUpper(intent[0]) + intent[1..];
        return $"Morgana ({capitalizedIntent})";
    }

    // Only reached on conversation resume: ConversationManagerActor sends this after loading a
    // persisted conversation whose last turn left an agent active, so this supervisor instance
    // (freshly created, activeAgent still null) can pick up the multi-turn state where it left off.
    private void HandleRestoreActiveAgent(Records.RestoreActiveAgent msg)
    {
        actorLogger.Info($"Restoring active agent: {msg.AgentIntent}");
        
        // Tell the router to rehydrate an agent for the given intent
        router.Tell(new Records.RestoreAgentRequest(msg.AgentIntent));
    }

    /// <summary>
    /// Completes the resume flow HandleRestoreActiveAgent started: records the rehydrated agent
    /// as active, or clears it if RouterActor couldn't recreate one for the given intent.
    /// </summary>
    private void HandleRestoreAgentResponse(Records.RestoreAgentResponse response)
    {
        if (response.AgentRef != null)
        {
            // RouterActor rehydrated the agent: picks up the multi-turn state exactly where the
            // persisted conversation left off.
            activeAgent = response.AgentRef;
            activeAgentIntent = response.AgentIntent;
            actorLogger.Info($"Active agent restored: {activeAgent.Path} with intent {activeAgentIntent}");
        }
        else
        {
            // No agent could be recreated for this intent: falls back to no active agent, so the
            // next message classifies as a brand-new request instead of routing to nothing.
            activeAgent = null;
            activeAgentIntent = null;
            actorLogger.Warning($"Could not restore agent for intent '{response.AgentIntent}' - clearing active agent");
        }
    }

    /// <inheritdoc/>
    protected override void RegisterCommonHandlers()
    {
        // Registers whatever handlers MorganaActor wires into every state (e.g. health checks) —
        // called from every state method in this class so those stay available regardless of FSM state.
        base.RegisterCommonHandlers();

        // Asks RouterActor to recreate the agent for a resumed conversation (see HandleRestoreActiveAgent).
        Receive<Records.RestoreActiveAgent>(HandleRestoreActiveAgent);
 
        // Records the recreated agent, or clears activeAgent if it couldn't be restored (see HandleRestoreAgentResponse).
        Receive<Records.RestoreAgentResponse>(HandleRestoreAgentResponse);
    }

    /// <summary>
    /// Disposes any OTel spans that are still open when the actor stops.
    /// Without this, a conversation terminated mid-turn (e.g. via the /end endpoint or a
    /// supervision failure) leaks <see cref="Activity"/> objects for the lifetime of the process.
    /// Child spans (guard, classifier) are disposed before the parent turn span.
    /// </summary>
    protected override void PostStop()
    {
        // Guard span, still open if the actor stopped before AwaitingGuardCheck ever got a response.
        if (guardSpan is not null)
        {
            guardSpan.SetStatus(ActivityStatusCode.Error, "actor stopped mid-turn");
            guardSpan.Dispose();
            guardSpan = null;
        }

        // Classifier span, still open if the actor stopped before AwaitingClassification ever got a response.
        if (classifierSpan is not null)
        {
            classifierSpan.SetStatus(ActivityStatusCode.Error, "actor stopped mid-turn");
            classifierSpan.Dispose();
            classifierSpan = null;
        }

        // Closes the turn span itself last, tagged with whatever agent was active — same error
        // status as the two children above.
        CloseTurnSpan(ActivityStatusCode.Error, "actor stopped mid-turn", intent: activeAgentIntent, completed: false);

        actorLogger.Info($"ConversationSupervisorActor stopped for {conversationId}");

        // Runs last, after this override's own span cleanup: chains into whatever MorganaActor's own PostStop does.
        base.PostStop();
    }

    #endregion
}