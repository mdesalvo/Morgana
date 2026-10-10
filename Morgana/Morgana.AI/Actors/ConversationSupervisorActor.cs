using System.Diagnostics;
using System.Globalization;
using Akka.Actor;
using Akka.Event;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Morgana.AI.Abstractions;
using Morgana.AI.Extensions;
using Morgana.AI.Interfaces;
using Morgana.AI;
using Morgana.Contracts;
using Status = Akka.Actor.Status;

namespace Morgana.AI.Actors;

/// <summary>
/// Main FSM orchestration actor: supervises conversation flow through guard check, classification,
/// agent routing and follow-up handling. Tracks active agent for multi-turn conversations.
/// 5 states: Idle, AwaitingGuardCheck, AwaitingClassification, AwaitingAgentResponse,
/// AwaitingFollowUpResponse. Manages OpenTelemetry context hierarchy via TurnContext.
/// </summary>
public class ConversationSupervisorActor : MorganaActor
{
    /// <summary>Delivers the welcome message to the conversation's channel.</summary>
    private readonly IChannelService channelService;

    /// <summary>Holds the conversation's settled channel, whose capabilities bound each turn.</summary>
    private readonly IChannelMetadataStore channelMetadataStore;

    /// <summary>Lists the configured intents that a welcome message or a disambiguation offers.</summary>
    private readonly IAgentConfigurationService agentConfigService;

    /// <summary>Writes the welcome message and its quick replies.</summary>
    private readonly IPresenterService presenterService;

    /// <summary>
    /// Holds which agent the conversation was left talking to, the one fact this supervisor must read
    /// back before its first turn: a process that restarted mid-exchange knows it from nowhere else.
    /// </summary>
    private readonly IConversationPersistenceService conversationPersistenceService;

    /// <summary>Moderates each message before anything else reads it.</summary>
    private readonly IActorRef guard;

    /// <summary>Ranks the intents of a message that no active agent is waiting for.</summary>
    private readonly IActorRef classifier;

    /// <summary>Reaches the agent of an intent and carries its answer back.</summary>
    private readonly IActorRef router;

    /// <summary>Morgana's own prompt, whose messages are what she says in her own voice: farewells, apologies.</summary>
    private readonly Records.Prompt morganaPrompt;

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
    /// Whether this supervisor has already taken the active agent from the conversation's record.
    /// Read once, on its first turn: from then on this supervisor is the one keeping it current.
    /// </summary>
    private bool hasReadPersistedActiveAgent;

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
    /// The silence that any in-flight phase of the turn may last before the supervisor gives up on it.
    /// </summary>
    private TimeSpan PhaseBudget
        => TimeSpan.FromSeconds(Convert.ToInt32(configuration["Morgana:ActorSystem:TimeoutSeconds"], CultureInfo.InvariantCulture));

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
        IConversationPersistenceService conversationPersistenceService,
        IConfiguration configuration) : base(conversationId, llmService, promptResolverService, configuration)
    {
        // The collaborators are fixed for the conversation: the supervisor only decides who works next.
        this.channelService = channelService;
        this.channelMetadataStore = channelMetadataStore;
        this.agentConfigService = agentConfigService;
        this.presenterService = presenterService;
        this.conversationPersistenceService = conversationPersistenceService;

        // The three workers are created before the first message so that a turn never waits on actor creation.
        // The wait is synchronous because a constructor cannot await.
        guard = Context.System.GetOrCreateActorAsync<GuardActor>(Constants.Actors.Guard, conversationId).GetAwaiter().GetResult();
        classifier = Context.System.GetOrCreateActorAsync<ClassifierActor>(Constants.Actors.Classifier, conversationId).GetAwaiter().GetResult();
        router = Context.System.GetOrCreateActorAsync<RouterActor>(Constants.Actors.Router, conversationId).GetAwaiter().GetResult();

        // Morgana's lines are read from her prompt at every turn that needs one, so it is resolved here once.
        morganaPrompt = promptResolverService.ResolveAsync(Constants.Morgana).GetAwaiter().GetResult();

        // A supervisor born for a conversation waits for its first message or presentation request.
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

        // The welcome message is written on conversation start and delivered once it is written.
        ReceiveAsync<Records.GeneratePresentationMessage>(HandlePresentationRequestAsync);
        ReceiveAsync<Records.PresentationContext>(HandlePresentationGenerated);

        // Every user message opens a turn at the guard.
        ReceiveAsync<Records.UserMessage>(HandleUserMessageAsync);

        RegisterCommonHandlers();
    }

    /// <summary>
    /// Opens the turn span and dispatches to <see cref="GuardActor"/>. Applies to both new
    /// requests and follow-ups: every user message goes through the guard check first.
    /// </summary>
    private async Task HandleUserMessageAsync(Records.UserMessage msg)
    {
        // The sender is the manager waiting for the answer: captured before the first await.
        IActorRef originalSender = Sender;

        actorLogger.Info("User message received, routing through guard check");

        // Settled before the turn starts, because the turn decides on it: with an agent left
        // mid-exchange this message is its answer, never a new request to classify.
        if (!hasReadPersistedActiveAgent)
        {
            await RestorePersistedActiveAgentAsync();
            hasReadPersistedActiveAgent = true;
        }

        // With no agent mid-exchange, this phrase is addressed to Morgana herself: she guards it,
        // she classifies it, she answers it unless she hands it to an agent. So she files it now,
        // before the guard has even seen it — which is what lets a client that reloads during the
        // guard or the classifier read back what it just said. An agent that was already active
        // saves the phrase into its own session instead, because that session is its record.
        bool noActiveAgent = activeAgent == null;
        if (noActiveAgent)
            await conversationPersistenceService.AppendOrchestratorMessagesAsync(
                conversationId,
                [new ChatMessage(ChatRole.User, msg.Text) { CreatedAt = DateTimeOffset.UtcNow }]);

        // The channel's budget for everything this turn produces, carried with the turn to each
        // agent request it ends up sending.
        ChannelMetadata channelMetadata = await channelMetadataStore.GetChannelMetadataAsync(conversationId);
        ChannelCapabilities turnCapabilities = channelMetadata.Capabilities;

        // Starts "morgana.turn", the root OTel span for this entire turn — the one unit of work a
        // trace viewer (Jaeger/Tempo) shows per user message, with every later stage (guard,
        // classifier, router, agent) recorded as a child of it. It's linked (ActivityLink), not
        // parented, to the HTTP request span the controller propagated in msg.TurnContext: this
        // actor keeps processing after the HTTP response has already returned to the client, so a
        // parent/child pair (which a trace UI expects to close together) would be the wrong shape.
        ActivityLink[] links = msg.TurnContext != default ? [new ActivityLink(msg.TurnContext)] : [];
        turnSpan = Telemetry.Source.StartActivity(Telemetry.TurnActivity, ActivityKind.Internal, parentContext: default, links: links);
        // A trace is found by conversation and shows the first 200 characters of what the user said.
        turnSpan?.SetTag(Telemetry.ConversationId, conversationId);
        turnSpan?.SetTag(Telemetry.TurnUserMessage, msg.Text.Length > 200 ? msg.Text[..200] : msg.Text);

        // Every later stage parents its span on this one.
        ActivityContext turnContext = turnSpan?.Context ?? default;

        // Starts "morgana.guard", the first child span under morgana.turn — it exists so the guard
        // check's own latency and compliance verdict show up as their own row in a trace, separate
        // from classification/routing/agent time. Started here, before the Tell to GuardActor
        // rather than when its response arrives in AwaitingGuardCheck, so the recorded duration
        // covers the full round-trip — mailbox/dispatch latency included, not just GuardActor's
        // own processing time once it picks the message up.
        guardSpan = Telemetry.Source.StartActivity(Telemetry.GuardActivity, ActivityKind.Internal, turnContext);
        guardSpan?.SetTag(Telemetry.ConversationId, conversationId);

        // The turn's context travels through the states, each of which adds what it learned.
        Records.ProcessingContext ctx = new Records.ProcessingContext(
            msg, originalSender, turnCapabilities, TurnContext: turnContext, UserMessageAlreadyStored: noActiveAgent);
        Become(() => AwaitingGuardCheck(ctx));

        // The guard sees the message before the classifier or an agent does.
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

        // The catch-all and label-less intents have nothing to show as a button, so the welcome offers
        // the displayable subset: the same filter that the classifier's collision check applies.
        List<Records.IntentDefinition> allIntents = await agentConfigService.GetIntentsAsync();
        Records.IntentCollection intentCollection = new Records.IntentCollection(allIntents);
        List<Records.IntentDefinition> displayableIntents = intentCollection.GetDisplayableIntents();

        // The presenter never throws. Its result goes to Self instead of being delivered inline, so that
        // writing the welcome and delivering it stay two failure domains: a channel outage is never
        // taken for a generation fault.
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

        // Only Id, Label and Value of each button reach the user: the presentation prompt never asks
        // the model for a termination flag, so a button here can never end the conversation.
        List<QuickReply> quickReplies = [.. ctx.LLMQuickReplies?.Select(qr => new QuickReply(qr.Id, qr.Label, qr.Value)) ?? []];

        try
        {
            // The welcome is Morgana's first word and the only one spoken before anyone has asked
            // anything: dated once here, so the record and the push agree and a returning client
            // recognises the greeting it already has.
            DateTime presentationTimestamp = DateTime.UtcNow;
            await conversationPersistenceService.AppendOrchestratorMessagesAsync(
                conversationId,
                [new ChatMessage(ChatRole.Assistant, ctx.Message) { CreatedAt = presentationTimestamp }]);

            // The channel service degrades the message to the channel's capabilities before the transport sees it.
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
    /// <param name="ctx">Processing context containing original message, sender and OTel TurnContext</param>
    private void AwaitingGuardCheck(Records.ProcessingContext ctx)
    {
        actorLogger.Info("→ State: AwaitingGuardCheck");

        // A guard that never answers must not hold the turn: the phase budget hands it to FailOpen.
        Context.SetReceiveTimeout(PhaseBudget);

        // The verdict is the normal ending of this state: a failure or a timeout below are the other two.
        ReceiveAsync<Records.GuardCheckResponse>(async response => {
            // The guard answered, so its window ends here: the next state arms its own, or none.
            Context.SetReceiveTimeout(null);

            // The span records the verdict and the latency of the guard round-trip.
            guardSpan?.SetTag(Telemetry.GuardCompliant, response.Compliant);
            if (!response.Compliant && response.Violation != null)
                guardSpan?.SetTag(Telemetry.GuardViolation, response.Violation);
            if (guardSpan is not null)
                Telemetry.GuardDuration.Record((DateTime.UtcNow - guardSpan.StartTimeUtc).TotalMilliseconds);
            guardSpan?.Dispose();
            guardSpan = null;

            // A rejection ends the turn here: no classification, routing or agent runs.
            // The next message enters the guard afresh and any active agent is left as it was.
            if (!response.Compliant)
            {
                actorLogger.Warning($"Message rejected by guard: {response.Violation}");

                // An agent was mid-exchange, so nobody stored the phrase at ingress: the agent
                // would have done it, but the guard just made sure it never runs. Morgana answers this
                // turn instead, so the phrase is hers to keep — without it the refusal would sit in
                // the transcript with nothing above it explaining what was refused.
                if (!ctx.UserMessageAlreadyStored)
                    await conversationPersistenceService.AppendOrchestratorMessagesAsync(
                        conversationId,
                        [new ChatMessage(ChatRole.User, ctx.OriginalMessage.Text) { CreatedAt = DateTime.UtcNow }]);

                // The guard's rejection text is the whole reply: no buttons, date or card.
                ctx.OriginalSender.Tell(new Records.ConversationResponse(
                    response.Violation!,
                    ctx.Classification?.Intent,
                    ctx.Classification?.Metadata,
                    activeAgentIntent != null ? GetAgentDisplayName(activeAgentIntent) : Constants.Morgana,
                    false,
                    null,
                    null,
                    null));

                // Rejections are counted apart from the per-turn counter that closing the turn feeds.
                Telemetry.GuardRejectionCounter.Add(1);

                CloseTurnSpan(intent: ctx.Classification?.Intent, completed: false);
                Become(Idle);
                return;
            }

            actorLogger.Info("Message passed guard check");
            ContinueAfterGuard(ctx);
        });

        // A guard that threw or went silent is not a verdict: both end in FailOpen.
        Receive<Status.Failure>(failure => FailOpen(failure.Cause.Message, failure.Cause));
        Receive<ReceiveTimeout>(_ => FailOpen("receive timeout", null));

        RegisterCommonHandlers();
        return;

        #region Locals
        // Open, not closed: an outage in moderation must not be able to take the product down for everyone.
        void FailOpen(string description, Exception? cause)
        {
            // The guard has ended its part, even with a failure: no reason to keep waiting.
            Context.SetReceiveTimeout(null);

            if (cause != null)
                actorLogger.Error(cause, "Guard check failed: {0}", description);
            else
                actorLogger.Error("Guard check failed: {0}", description);
            actorLogger.Warning("Guard check failed, failing open (allowing message)");

            // The span ends as a failure instead of a compliance verdict.
            guardSpan?.SetStatus(ActivityStatusCode.Error, description);
            if (cause != null)
                guardSpan?.AddException(cause);
            guardSpan?.Dispose();
            guardSpan = null;

            // The message passes as a compliant one would: the routing decision does not depend on the guard.
            ContinueAfterGuard(ctx);
        }
        #endregion
    }

    /// <summary>
    /// Sends a message that passed the guard to the active agent or, when there is none, to classification.
    /// </summary>
    private void ContinueAfterGuard(Records.ProcessingContext ctx)
    {
        // An active agent means the message is the next turn of an exchange in progress, not a new
        // request: classification is skipped and the same agent instance receives it.
        if (activeAgent != null)
        {
            actorLogger.Info($"Active agent exists, routing to follow-up flow with agent {activeAgent.Path}");

            Become(() => AwaitingFollowUpResponse(ctx.OriginalSender));

            // The follow-up path never classifies, so the agent receives none.
            activeAgent.Tell(new Records.AgentRequest(
                ctx.OriginalMessage.ConversationId,
                ctx.OriginalMessage.Text,
                null,
                ctx.TurnContext,
                ctx.ChannelCapabilities,
                ctx.UserMessageAlreadyStored));
            return;
        }

        actorLogger.Info("No active agent, proceeding to classification for new request");

        // Opened before the Tell, like the guard span, so that its duration covers the whole
        // round-trip and not only the classifier's own processing.
        classifierSpan = Telemetry.Source.StartActivity(Telemetry.ClassifierActivity, ActivityKind.Internal, ctx.TurnContext);
        classifierSpan?.SetTag(Telemetry.ConversationId, conversationId);

        Become(() => AwaitingClassification(ctx));
        classifier.Tell(ctx.OriginalMessage);
    }

    /// <summary>
    /// AwaitingClassification state: waiting for intent classification result from ClassifierActor.
    /// Opens a morgana.classifier child span using the TurnContext from ProcessingContext.
    /// </summary>
    /// <param name="ctx">Processing context containing original message, sender and OTel TurnContext</param>
    private void AwaitingClassification(Records.ProcessingContext ctx)
    {
        actorLogger.Info("→ State: AwaitingClassification");

        // A classifier that never answers must not hold the turn: the phase budget hands it to FallbackToOther.
        Context.SetReceiveTimeout(PhaseBudget);

        // The result is the normal ending of this state: a failure or a timeout below are the other two.
        ReceiveAsync<Records.ClassificationResult>(async classification => {
            // The classifier answered, so its window ends here: the next state arms its own, or none.
            Context.SetReceiveTimeout(null);

            actorLogger.Info($"Classification result: {classification.Intent}");

            // The span records the top intent with its confidence and the full ranking plus the latency of the round-trip.
            classifierSpan?.SetTag(Telemetry.ClassificationIntent, classification.Intent);
            classifierSpan?.SetTag(Telemetry.ClassificationMetadata, classification.Metadata);
            if (classification.Metadata.TryGetValue("confidence", out string? confidence))
                classifierSpan?.SetTag(Telemetry.ClassificationConfidence, confidence);
            if (classifierSpan is not null)
                Telemetry.ClassifierDuration.Record((DateTime.UtcNow - classifierSpan.StartTimeUtc).TotalMilliseconds);
            classifierSpan?.Dispose();
            classifierSpan = null;

            // A colliding classification is diverted to the user before it ever reaches the router:
            // no agent is invoked and none becomes active.
            if (classification.Metadata.TryGetValue("ambiguousIntents", out string? collidingIntentNames))
            {
                await SendDisambiguationAsync(ctx with { Classification = classification }, collidingIntentNames);
                return;
            }

            DispatchToRouter(ctx, classification);
        });

        // A classifier that threw or went silent is not a ranking: both end in FallbackToOther.
        Receive<Status.Failure>(failure => FallbackToOther(failure.Cause.Message, failure.Cause));
        Receive<ReceiveTimeout>(_ => FallbackToOther("receive timeout", null));

        RegisterCommonHandlers();
        return;

        #region Locals
        // The message is treated as an intent that no agent handles, so the user still gets an answer.
        void FallbackToOther(string description, Exception? cause)
        {
            // The classifier has ended its part, even with a failure: no reason to keep waiting.
            Context.SetReceiveTimeout(null);

            if (cause != null)
                actorLogger.Error(cause, "Classification failed: {0}", description);
            else
                actorLogger.Error("Classification failed: {0}", description);

            // The span ends as a failure instead of a classification result.
            classifierSpan?.SetStatus(ActivityStatusCode.Error, description);
            if (cause != null)
                classifierSpan?.AddException(cause);
            classifierSpan?.Dispose();
            classifierSpan = null;

            // Shaped like a real result, with the same "confidence" key and string type, so that nothing
            // downstream needs a special case; "error" is the one extra key, kept for diagnostics.
            Records.ClassificationResult fallbackClassification = new Records.ClassificationResult(
                Constants.Intents.Other,
                new Dictionary<string, string>
                {
                    ["confidence"] = "0.00",
                    ["error"] = $"classification_failed: {description}"
                });

            // The router has no agent for "other" by design and answers with its unrecognized-intent text.
            actorLogger.Info("Falling back to 'other' intent");

            DispatchToRouter(ctx, fallbackClassification);
        }
        #endregion
    }

    /// <summary>
    /// Hands a classified request to the router and waits for the agent's answer.
    /// </summary>
    private void DispatchToRouter(Records.ProcessingContext ctx, Records.ClassificationResult classification)
    {
        // The classification travels with the context into the next state.
        Records.ProcessingContext updatedCtx = ctx with { Classification = classification };

        // Opened before the Tell, like the guard and classifier spans, so that its duration covers
        // the whole round-trip. It is scoped to this call because nothing else needs to close it.
        using Activity? routerSpan = Telemetry.Source.StartActivity(
            Telemetry.RouterActivity,
            ActivityKind.Internal,
            ctx.TurnContext);
        routerSpan?.SetTag(Telemetry.RouterIntent, classification.Intent);

        Become(() => AwaitingAgentResponse(updatedCtx));

        router.Tell(new Records.AgentRequest(
            ctx.OriginalMessage.ConversationId,
            ctx.OriginalMessage.Text,
            classification,
            ctx.TurnContext,
            ctx.ChannelCapabilities,
            ctx.UserMessageAlreadyStored));
    }

    /// <summary>
    /// AwaitingAgentResponse state: waiting for specialized agent to process the request.
    /// Annotates the turn span with agent name and intent on response.
    /// </summary>
    /// <param name="ctx">Processing context containing original message, sender and classification</param>
    private void AwaitingAgentResponse(Records.ProcessingContext ctx)
    {
        actorLogger.Info("→ State: AwaitingAgentResponse");

        // The budget bounds silence, not the turn: every chunk renews it, so only an agent that goes
        // quiet for a whole window times out and a slow but live one never does.
        Context.SetReceiveTimeout(PhaseBudget);

        // Neither the router nor the agent behind it answered within the window.
        Receive<ReceiveTimeout>(_ =>
        {
            actorLogger.Error($"Timeout waiting for agent response (classification: {ctx.Classification?.Intent})");

            // The window has fired and must not fire again for whatever runs next.
            Context.SetReceiveTimeout(null);

            // No agent was confirmed for this turn yet, so there is no active agent to drop:
            // the timeout in AwaitingFollowUpResponse clears one because it was already set.
            ctx.OriginalSender.Tell(new Records.ConversationResponse(
                MorganaMessage(Constants.Messages.Timeout),
                ctx.Classification?.Intent,
                ctx.Classification?.Metadata,
                GetAgentDisplayName(ctx.Classification?.Intent),
                false,
                null,
                null,
                null));

            CloseTurnSpan(ActivityStatusCode.Error, "Timeout waiting for agent response", intent: ctx.Classification?.Intent, completed: false);
            Become(Idle);
        });

        // The user sees the answer as it is written while each chunk proves the agent alive.
        Receive<Records.AgentStreamChunk>(chunk =>
        {
            Context.SetReceiveTimeout(PhaseBudget);
            ctx.OriginalSender.Tell(chunk);
        });

        // Renews the same window for work the user cannot see: a tool running, a colleague answering.
        // The client is told nothing, having nothing to show; an agent that stops sending
        // these is one that has genuinely stopped.
        Receive<Records.AgentStillWorking>(_ =>
            Context.SetReceiveTimeout(PhaseBudget));

        // An agent that ran answers with ActiveAgentResponse; the router's own fallback for an intent that
        // no agent handles is the plain AgentResponse below.
        Receive<Records.ActiveAgentResponse>(response =>
        {
            // The agent answered and Idle arms no window of its own.
            Context.SetReceiveTimeout(null);

            try
            {
                // The name under which the user sees the answer.
                string agentName = GetAgentDisplayName(ctx.Classification?.Intent);

                actorLogger.Info($"Received ActiveAgentResponse from {response.AgentRef.Path}, " +
                                 $"completed: {response.IsCompleted}, " +
                                 $"quickReplies: {response.QuickReplies?.Count ?? 0}");

                // A completed turn hands the conversation back to Morgana, while an open one keeps the
                // agent active so that the next message skips classification.
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

                // The reply carries the date the agent recorded it under, so that a catching-up client recognises it.
                ctx.OriginalSender.Tell(new Records.ConversationResponse(
                    response.Response,
                    ctx.Classification?.Intent,
                    ctx.Classification?.Metadata,
                    agentName,
                    response.IsCompleted,
                    response.QuickReplies,
                    response.RecordedTimestamp,
                    response.RichCard));

                // The agent has finished: Morgana takes the conversation back and says so, behind
                // the answer above rather than in place of it.
                if (response.IsCompleted)
                    TellAgentFarewell(ctx.OriginalSender, agentName);

                CloseTurnSpan(intent: ctx.Classification?.Intent, completed: response.IsCompleted);

                // The follow-up is carried by activeAgent, so the FSM returns to Idle either way.
                Become(Idle);
            }
            catch (Exception ex)
            {
                actorLogger.Error(ex, "Error processing ActiveAgentResponse");

                // The agent's answer could not be handled, so the next message starts a fresh
                // classification instead of continuing with an agent in an unknown state.
                activeAgent = null;
                activeAgentIntent = null;

                // The user is apologised to in place of the answer that was lost.
                ctx.OriginalSender.Tell(new Records.ConversationResponse(
                    MorganaMessage(Constants.Messages.GenericError),
                    ctx.Classification?.Intent,
                    ctx.Classification?.Metadata,
                    GetAgentDisplayName(ctx.Classification?.Intent),
                    false,
                    null,
                    null,
                    null));

                CloseTurnSpan(ActivityStatusCode.Error, ex.Message, intent: ctx.Classification?.Intent, completed: false, exception: ex);
                Become(Idle);
            }
        });

        // The router's fallback answer, sent when no agent handles the classified intent.
        Receive<Records.AgentResponse>(response =>
        {
            // The router answered in time and Idle arms no window of its own.
            Context.SetReceiveTimeout(null);

            try
            {
                actorLogger.Info("Received fallback response from router (no specialized agent)");

                // No agent stands behind this reply, so the turn is complete by construction and
                // the speaker is Morgana herself.
                ctx.OriginalSender.Tell(new Records.ConversationResponse(
                    response.Response,
                    ctx.Classification?.Intent,
                    ctx.Classification?.Metadata,
                    Constants.Morgana,
                    true,
                    response.QuickReplies,
                    null,
                    response.RichCard));

                CloseTurnSpan(intent: ctx.Classification?.Intent, completed: true);
                Become(Idle);
            }
            catch (Exception ex)
            {
                actorLogger.Error(ex, "Error processing fallback AgentResponse");

                // The user is apologised to in place of the fallback text that was lost.
                ctx.OriginalSender.Tell(new Records.ConversationResponse(
                    MorganaMessage(Constants.Messages.GenericError),
                    ctx.Classification?.Intent,
                    ctx.Classification?.Metadata,
                    Constants.Morgana,
                    false,
                    null,
                    DateTime.UtcNow,
                    null));

                CloseTurnSpan(ActivityStatusCode.Error, ex.Message, intent: ctx.Classification?.Intent, completed: false, exception: ex);
                Become(Idle);
            }
        });

        // A provider refusal on the agent's own call is answered like a guard rejection.
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

        // Silence is bounded and every chunk renews the window, as in AwaitingAgentResponse.
        Context.SetReceiveTimeout(PhaseBudget);

        // The active agent did not answer this follow-up within the window.
        Receive<ReceiveTimeout>(_ =>
        {
            actorLogger.Error($"Timeout waiting for follow-up response from active agent (intent: {activeAgentIntent})");

            // The window has fired and must not fire again for whatever runs next.
            Context.SetReceiveTimeout(null);

            // The silent agent is dropped, so the next message is classified as a new request.
            // The intent is kept first because closing the turn span still tags it.
            string? timedOutIntent = activeAgentIntent;
            activeAgent = null;
            activeAgentIntent = null;

            // This state carries no classification, so the apology has no intent or metadata to attach.
            originalSender.Tell(new Records.ConversationResponse(
                MorganaMessage(Constants.Messages.Timeout),
                null,
                null,
                Constants.Morgana,
                false,
                null,
                DateTime.UtcNow,
                null));

            CloseTurnSpan(ActivityStatusCode.Error, "Timeout waiting for follow-up response", intent: timedOutIntent, completed: false);
            Become(Idle);
        });

        // The user sees the answer as it is written while each chunk proves the agent alive.
        Receive<Records.AgentStreamChunk>(chunk =>
        {
            Context.SetReceiveTimeout(PhaseBudget);
            originalSender.Tell(chunk);
        });

        // Work without text renews the window: the follow-up turn is where an active agent consults a colleague.
        Receive<Records.AgentStillWorking>(_ =>
            Context.SetReceiveTimeout(PhaseBudget));

        // The active agent's reply to this follow-up.
        Receive<Records.AgentResponse>(response =>
        {
            // The agent answered. The FSM returns to Idle whether or not it completed, because
            // the exchange continues through activeAgent and not through this state.
            Context.SetReceiveTimeout(null);

            // Kept before a completion clears it, so that the turn span can still be tagged.
            string? currentIntent = activeAgentIntent;
            try
            {
                string agentName = currentIntent != null ? GetAgentDisplayName(currentIntent) : Constants.Morgana;

                // The agent leaves only when it says it is done: while it keeps the exchange open it stays active.
                if (response.IsCompleted)
                {
                    actorLogger.Info("Active agent signaled completion, clearing active agent");
                    activeAgent = null;
                    activeAgentIntent = null;
                }

                // A follow-up is not classified, so the reply carries no intent or metadata.
                originalSender.Tell(new Records.ConversationResponse(
                    response.Response,
                    null,
                    null,
                    agentName,
                    response.IsCompleted,
                    response.QuickReplies,
                    response.RecordedTimestamp,
                    response.RichCard));

                // The agent has finished: Morgana takes the conversation back and says so, behind
                // the answer above rather than in place of it.
                if (response.IsCompleted)
                    TellAgentFarewell(originalSender, agentName);

                CloseTurnSpan(intent: currentIntent, completed: response.IsCompleted);
                Become(Idle);
            }
            catch (Exception ex)
            {
                actorLogger.Error(ex, "Error processing follow-up AgentResponse");

                // The failure happened while handling the agent's answer, so no known-good state is left to continue with.
                activeAgent = null;
                activeAgentIntent = null;

                // The user is apologised to in place of the answer that was lost.
                originalSender.Tell(new Records.ConversationResponse(
                    MorganaMessage(Constants.Messages.GenericError),
                    null,
                    null,
                    Constants.Morgana,
                    false,
                    null,
                    DateTime.UtcNow,
                    null));

                CloseTurnSpan(ActivityStatusCode.Error, ex.Message, intent: currentIntent, completed: false, exception: ex);
                Become(Idle);
            }
        });

        // A provider refusal on the active agent's own call is answered like a guard rejection.
        ReceiveAsync<Records.ContentFilterRejection>(_ => HandleContentFilterRejectionAsync(originalSender));

        RegisterCommonHandlers();
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Closes the turn span, records metrics and transitions to Idle.
    /// Called at every point where the FSM returns to Idle after processing a user message.
    /// </summary>
    private void CloseTurnSpan(
        ActivityStatusCode status = ActivityStatusCode.Ok,
        string? description = null,
        string? intent = null,
        bool? completed = null,
        Exception? exception = null)
    {
        // A turn is closed once: a second call, or PostStop after a closed turn, finds nothing to do.
        if (turnSpan is not null)
        {
            // Only a failed turn is marked: a completed one keeps the span's default status.
            if (status == ActivityStatusCode.Error)
            {
                turnSpan.SetStatus(status, description);
                if (exception is not null)
                    turnSpan.AddException(exception);
            }

            // The span is still running here, so its duration is read from the clock.
            double durationMs = (turnSpan.Duration != TimeSpan.Zero)
                ? turnSpan.Duration.TotalMilliseconds
                : (DateTime.UtcNow - turnSpan.StartTimeUtc).TotalMilliseconds;

            // Volume and latency are broken down by intent and completion on a dashboard.
            Telemetry.TurnDuration.Record(durationMs);
            Telemetry.TurnCounter.Add(1,
                new KeyValuePair<string, object?>("intent", intent ?? "unknown"),
                new KeyValuePair<string, object?>("completed", completed ?? false));

            // The span ends with the turn and the next turn opens its own.
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
        // The classifier names the colliding intents as a comma-separated list.
        string[] intentNames = collidingIntentNames.Split(',', StringSplitOptions.RemoveEmptyEntries);

        actorLogger.Info($"Classification ambiguous, offering disambiguation among [{collidingIntentNames}]");

        // A bare intent name becomes a button only with its definition: the label to show and a sample phrase to send.
        List<Records.IntentDefinition> allIntents = await agentConfigService.GetIntentsAsync();
        Dictionary<string, Records.IntentDefinition> intentsByName =
            allIntents.ToDictionary(intent => intent.Name, StringComparer.OrdinalIgnoreCase);

        // One button per colliding intent, most confident first. Pressing it sends the intent's sample
        // phrase as the user's next message, which classifies unambiguously.
        List<QuickReply> quickReplies =
        [
            .. intentNames
                .Where(intentsByName.ContainsKey)
                .Select(name => intentsByName[name])
                .Select(intent => new QuickReply(intent.Name, intent.Label, intent.DefaultValue))
        ];

        // The question that accompanies the buttons is worded in the classifier's messages.
        Records.Prompt classifierPrompt = await promptResolverService.ResolveAsync(Constants.Prompts.Classifier);
        string disambiguationMessage = classifierPrompt.GetMessage(Constants.Messages.Disambiguation);

        // The question goes straight to the user, as a guard rejection does. It is not completed because
        // Morgana waits for the user, yet no agent is active: the answer is a fresh turn from the guard.
        ctx.OriginalSender.Tell(new Records.ConversationResponse(
            disambiguationMessage,
            ctx.Classification?.Intent,
            ctx.Classification?.Metadata,
            Constants.Morgana,
            false,
            quickReplies,
            null,
            null));

        CloseTurnSpan(intent: ctx.Classification?.Intent, completed: false);
        Become(Idle);
    }

    /// <summary>
    /// Handles a content filter rejection from an agent as if it were a guard rejection.
    /// Uses the same rejection shape and increments the guard rejection counter.
    /// </summary>
    private Task HandleContentFilterRejectionAsync(IActorRef originalSender)
    {
        // Every awaiting state arms a window and a refusal can arrive in any of them.
        Context.SetReceiveTimeout(null);

        actorLogger.Warning("Content filter rejection received from agent, treating as guard rejection");

        // The shape of a guard rejection: the speaker is the active agent mid-follow-up and Morgana otherwise.
        originalSender.Tell(new Records.ConversationResponse(
            "Content policy violation",
            activeAgentIntent,
            null,
            activeAgentIntent != null ? GetAgentDisplayName(activeAgentIntent) : Constants.Morgana,
            false,
            null,
            DateTime.UtcNow,
            null));

        // A content-policy block counts as a guard rejection either way.
        Telemetry.GuardRejectionCounter.Add(1);

        CloseTurnSpan(intent: activeAgentIntent, completed: false);
        Become(Idle);

        return Task.CompletedTask;
    }

    /// <summary>
    /// What Morgana says in her own voice under <paramref name="messageName"/>, as morgana.json words it.
    /// </summary>
    private string MorganaMessage(string messageName)
        => morganaPrompt.GetMessage(messageName);

    /// <summary>
    /// Says the line that closes an agent's engagement and brings the conversation back to Morgana,
    /// sent right behind that agent's own last answer so the two arrive in the order they were said.
    /// </summary>
    /// <remarks>
    /// Only a specialised agent earns one: Morgana finishing a turn of her own is just a turn. The
    /// line is Morgana's own message, dated when it was said and the same in every channel.
    /// </remarks>
    private void TellAgentFarewell(IActorRef sender, string departingAgentName)
    {
        // Morgana finishing a turn of her own has no engagement to close.
        if (string.Equals(departingAgentName, Constants.Morgana, StringComparison.OrdinalIgnoreCase))
            return;

        string farewellTemplate = MorganaMessage(Constants.Messages.AgentExit);

        // A deployment that words no farewell says none.
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

    /// <summary>
    /// Builds the display name shown to the client for a given intent: the bare persona
    /// or the persona qualified by the intent when one is available.
    /// </summary>
    private string GetAgentDisplayName(string? intent)
    {
        // No intent, or the catch-all Intents.Other: shown as the bare persona, with no agent name.
        if (string.IsNullOrEmpty(intent) || string.Equals(intent, Constants.Intents.Other, StringComparison.OrdinalIgnoreCase))
            return Constants.Morgana;

        // A specialised agent is shown as the persona qualified by its capitalised intent.
        string capitalizedIntent = char.ToUpperInvariant(intent[0]) + intent[1..];
        return $"Morgana ({capitalizedIntent})";
    }

    /// <summary>
    /// Takes back the agent the conversation was left talking to, as its record states it, then has
    /// the router bring that agent up. When nothing is active on record or the router cannot bring
    /// that agent up, the conversation has none: its next message is classified as a new request.
    /// </summary>
    private async Task RestorePersistedActiveAgentAsync()
    {
        // The record names the agent that the conversation was left with, if any.
        string? persistedAgentIntent = await conversationPersistenceService.GetMostRecentActiveAgentAsync(conversationId);
        if (persistedAgentIntent is null)
            return;

        actorLogger.Info($"Restoring active agent: {persistedAgentIntent}");

        try
        {
            // The turn about to start routes by this answer, so it is had before the turn goes on.
            // A one-off lookup: nothing streams and the router asks nothing back of this supervisor.
            Records.RestoreAgentResponse response = await router.Ask<Records.RestoreAgentResponse>(
                new Records.RestoreAgentRequest(persistedAgentIntent),
                PhaseBudget);

            // A router that finds no agent type answers with a null reference, which leaves the conversation with none.
            activeAgent = response.AgentRef;
            activeAgentIntent = response.AgentRef is null ? null : response.AgentIntent;
        }
        catch (Exception ex)
        {
            // An agent that cannot be brought up leaves the conversation with none: the next message is classified afresh.
            actorLogger.Error(ex, $"Router did not bring up agent '{persistedAgentIntent}'");
        }

        // The outcome is logged once, whichever way the restoration ended.
        if (activeAgent is null)
            actorLogger.Warning($"Could not restore agent for intent '{persistedAgentIntent}' - no active agent");
        else
            actorLogger.Info($"Active agent restored: {activeAgent.Path} with intent {activeAgentIntent}");
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

        // The turn span closes after its children and with the same error status.
        CloseTurnSpan(ActivityStatusCode.Error, "actor stopped mid-turn", intent: activeAgentIntent, completed: false);

        actorLogger.Info($"ConversationSupervisorActor stopped for {conversationId}");

        base.PostStop();
    }

    #endregion
}