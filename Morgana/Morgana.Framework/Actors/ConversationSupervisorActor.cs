using System.Text.Json;
using Akka.Actor;
using Akka.Event;
using Morgana.Framework.Abstractions;
using Morgana.Framework.Extensions;
using Morgana.Framework.Interfaces;

namespace Morgana.Framework.Actors;

/// <summary>
/// Main orchestration actor that supervises the conversation flow through a finite state machine.
/// Coordinates guard checks, intent classification, agent routing, and follow-up handling.
/// Manages presentation generation and tracks active agent state for multi-turn conversations.
/// </summary>
/// <remarks>
/// <para><strong>State Machine:</strong></para>
/// <list type="bullet">
/// <item><term>Idle</term><description>Waiting for user messages. Routes to guard check or active agent follow-up.</description></item>
/// <item><term>AwaitingGuardCheck</term><description>Waiting for content moderation result from GuardActor.</description></item>
/// <item><term>AwaitingClassification</term><description>Waiting for intent classification result from ClassifierActor.</description></item>
/// <item><term>AwaitingAgentResponse</term><description>Waiting for specialized agent to process the request.</description></item>
/// <item><term>AwaitingFollowUpResponse</term><description>Waiting for active agent to process follow-up message.</description></item>
/// </list>
/// <para><strong>Active Agent Tracking:</strong></para>
/// <para>When an agent signals incomplete processing (IsCompleted = false), the supervisor remembers the active agent
/// and routes subsequent messages directly to it until the agent signals completion.</para>
/// </remarks>
public class ConversationSupervisorActor : MorganaActor
{
    private readonly IActorRef guard;
    private readonly IActorRef classifier;
    private readonly IActorRef router;
    private readonly ISignalRBridgeService signalRBridgeService;
    private readonly IAgentConfigurationService agentConfigService;

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

        guard = Context.System.GetOrCreateActor<GuardActor>("guard", conversationId).GetAwaiter().GetResult();
        classifier = Context.System.GetOrCreateActor<ClassifierActor>("classifier", conversationId).GetAwaiter().GetResult();
        router = Context.System.GetOrCreateActor<RouterActor>("router", conversationId).GetAwaiter().GetResult();

        Idle();
    }

    #region State Behaviors

    /// <summary>
    /// Idle state: waiting for user messages or presentation requests.
    /// Routes messages to guard check (new requests) or active agent (follow-ups).
    /// </summary>
    private void Idle()
    {
        actorLogger.Info("→ State: Idle");

        // Generate and send welcome message with quick reply buttons on conversation start
        ReceiveAsync<Records.GeneratePresentationMessage>(HandlePresentationRequestAsync);
        ReceiveAsync<Records.PresentationContext>(HandlePresentationGenerated);

        // Handle incoming user messages:
        // - If active agent exists (multi-turn conversation): route directly to that agent → AwaitingFollowUpResponse
        // - Otherwise (new request): send to GuardActor for content moderation → AwaitingGuardCheck
        ReceiveAsync<Records.UserMessage>(async msg =>
        {
            IActorRef originalSender = Sender;

            if (activeAgent != null)
            {
                actorLogger.Info("Active agent detected, routing to follow-up flow");

                await EngageActiveAgentInFollowupAsync(msg, originalSender);
            }
            else
            {
                actorLogger.Info("No active agent, starting new request flow");

                Records.ProcessingContext ctx = new Records.ProcessingContext(msg, originalSender);

                _ = guard.Ask<Records.GuardCheckResponse>(
                            new Records.GuardCheckRequest(msg.ConversationId, msg.Text), TimeSpan.FromSeconds(60))
                         .PipeTo(
                            Self,
                            success: response => new Records.GuardCheckContext(response, ctx),
                            failure: ex => new Status.Failure(ex));

                Become(() => AwaitingGuardCheck(ctx));
            }
        });

        Receive<Status.Failure>(HandleUnexpectedFailure);

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

            actorLogger.Info("Calling LLM for presentation generation");

            // Call LLM to generate structured JSON
            string llmResponse = await llmService.CompleteWithSystemPromptAsync(
                conversationId,
                systemPrompt,
                "Generate the presentation message");

            actorLogger.Info($"LLM raw response: {llmResponse}");

            // Parse LLM response
            Records.PresentationResponse? presentationResponse =
                JsonSerializer.Deserialize<Records.PresentationResponse>(llmResponse);

            if (presentationResponse == null)
                throw new InvalidOperationException("LLM returned null presentation response");

            actorLogger.Info($"LLM generated presentation with {presentationResponse.QuickReplies.Count} quick replies");

            // Convert to internal format
            Self.Tell(new Records.PresentationContext(presentationResponse.Message, displayableIntents)
            {
                LLMQuickReplies = presentationResponse.QuickReplies
            });
        }
        catch (Exception ex)
        {
            actorLogger.Error(ex, "LLM presentation generation failed, using fallback");

            // Fallback: Generate from intents directly
            Records.Prompt presentationPrompt = await promptResolverService.ResolveAsync("Presentation");

            string fallbackMessage = presentationPrompt.GetAdditionalProperty<string>("FallbackMessage");

            // Build fallback quick replies from intents
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
    /// AwaitingGuardCheck state: waiting for content moderation result from GuardActor.
    /// On pass: transitions to AwaitingClassification.
    /// On fail: sends violation message to client and returns to Idle.
    /// </summary>
    /// <param name="ctx">Processing context containing original message and sender</param>
    private void AwaitingGuardCheck(Records.ProcessingContext ctx)
    {
        actorLogger.Info("→ State: AwaitingGuardCheck");

        // Handle content moderation result from GuardActor:
        // - If compliant: forward to ClassifierActor for intent detection → AwaitingClassification
        // - If violation detected: send rejection message to user and return → Idle
        ReceiveAsync<Records.GuardCheckContext>(async wrapper =>
        {
            if (!wrapper.Response.Compliant)
            {
                actorLogger.Warning($"Message rejected by guard: {wrapper.Response.Violation}");

                Records.Prompt guardPrompt = await promptResolverService.ResolveAsync("Guard");
                string guardAnswer = guardPrompt.GetAdditionalProperty<string>("GuardAnswer")
                    .Replace("((violation))", wrapper.Response.Violation ?? "Content policy violation");

                wrapper.Context.OriginalSender.Tell(new Records.ConversationResponse(
                    guardAnswer, null, null, "Morgana", false));

                Become(Idle);
                return;
            }

            actorLogger.Info("Message passed guard check, proceeding to classification");

            _ = classifier
                    .Ask<Records.ClassificationResult>(
                        wrapper.Context.OriginalMessage, TimeSpan.FromSeconds(60))
                    .PipeTo(
                        Self,
                        success: result => new Records.ClassificationContext(result, wrapper.Context, null),
                        failure: ex => new Status.Failure(ex));

            Become(() => AwaitingClassification(ctx));
        });

        Receive<Status.Failure>(failure =>
        {
            actorLogger.Error(failure.Cause, "Guard check failed");

            ctx.OriginalSender.Tell(new Records.ConversationResponse("An internal error occurred.", null, null, "Morgana", false));

            Become(Idle);
        });

        // Re-register common handlers for FSM behavior consistency
        RegisterCommonHandlers();
    }

    /// <summary>
    /// AwaitingClassification state: waiting for intent classification result from ClassifierActor.
    /// On success: forwards to RouterActor and transitions to AwaitingAgentResponse.
    /// On failure: sends error message to client and returns to Idle.
    /// </summary>
    /// <param name="ctx">Processing context containing original message and sender</param>
    private void AwaitingClassification(Records.ProcessingContext ctx)
    {
        actorLogger.Info("→ State: AwaitingClassification");

        // Handle intent classification result from ClassifierActor:
        // - Classification successful: forward to RouterActor to find appropriate agent → AwaitingAgentResponse
        // - Updates processing context with classification metadata for agent routing
        ReceiveAsync<Records.ClassificationContext>(async wrapper =>
        {
            actorLogger.Info($"Classification result: {wrapper.Classification.Intent}");

            Records.ProcessingContext updatedCtx = ctx with { Classification = wrapper.Classification };

            _ = router
                    .Ask<object>(
                        new Records.AgentRequest(
                            wrapper.Context.OriginalMessage.ConversationId,
                            wrapper.Context.OriginalMessage.Text,
                            wrapper.Classification), TimeSpan.FromSeconds(60))
                    .PipeTo(
                        Self,
                        success: response => new Records.AgentContext(response, updatedCtx),
                        failure: ex => new Status.Failure(ex));

            Become(() => AwaitingAgentResponse(updatedCtx));
        });

        Receive<Status.Failure>(failure =>
        {
            actorLogger.Error(failure.Cause, "Classification failed");

            ctx.OriginalSender.Tell(new Records.ConversationResponse("An internal error occurred.", null, null, "Morgana", false));

            Become(Idle);
        });

        // Re-register common handlers for FSM behavior consistency
        RegisterCommonHandlers();
    }

    /// <summary>
    /// AwaitingAgentResponse state: waiting for specialized agent to process the request.
    /// Handles both ActiveAgentResponse (from specialized agents) and AgentResponse (from router fallback).
    /// Updates active agent tracking based on agent completion status.
    /// </summary>
    /// <param name="ctx">Processing context containing original message, sender, and classification</param>
    /// <remarks>
    /// If agent signals incomplete (IsCompleted = false), the agent becomes "active" and subsequent messages
    /// are routed directly to it until it signals completion.
    /// </remarks>
    private void AwaitingAgentResponse(Records.ProcessingContext ctx)
    {
        actorLogger.Info("→ State: AwaitingAgentResponse");

        // Handle response from specialized agent (via RouterActor):
        // - ActiveAgentResponse: response from intent-specific agent (BillingAgent, ContractAgent, etc.)
        // - AgentResponse: fallback response from RouterActor (no agent found for intent)
        // 
        // If agent signals IsCompleted=false (multi-turn conversation needed):
        //   - Set activeAgent reference for future follow-up messages
        //   - Subsequent user messages will route directly to this agent (bypass classification)
        // If agent signals IsCompleted=true:
        //   - Clear activeAgent reference
        //   - Next user message will go through full flow (guard → classifier → router)
        //
        // Then return → Idle to await next user message
        ReceiveAsync<Records.AgentContext>(async wrapper =>
        {
            string agentName = GetAgentDisplayName(ctx.Classification?.Intent);

            switch (wrapper.Response)
            {
                case Records.ActiveAgentResponse activeAgentResponse:
                {
                    int quickRepliesCount = activeAgentResponse.QuickReplies?.Count ?? 0;
                    actorLogger.Info($"Received ActiveAgentResponse from {activeAgentResponse.AgentRef.Path}, completed: {activeAgentResponse.IsCompleted}, quickReplies: {quickRepliesCount}");

                    if (!activeAgentResponse.IsCompleted)
                    {
                        activeAgent = activeAgentResponse.AgentRef;
                        activeAgentIntent = ctx.Classification?.Intent;

                        actorLogger.Info($"Active agent set to {activeAgent.Path} with intent {activeAgentIntent}");
                    }
                    else
                    {
                        activeAgent = null;
                        activeAgentIntent = null;

                        actorLogger.Info("Agent completed and cleared");
                    }

                    wrapper.Context.OriginalSender.Tell(new Records.ConversationResponse(
                        activeAgentResponse.Response,
                        wrapper.Context.Classification?.Intent,
                        wrapper.Context.Classification?.Metadata,
                        agentName,
                        activeAgentResponse.IsCompleted,
                        activeAgentResponse.QuickReplies));

                    break;
                }
                case Records.AgentResponse routerFallbackResponse:
                {
                    actorLogger.Warning("Received AgentResponse instead of ActiveAgentResponse (router fallback)");

                    if (!routerFallbackResponse.IsCompleted)
                    {
                        activeAgent = router;
                        activeAgentIntent = ctx.Classification?.Intent;

                        actorLogger.Warning($"Fallback active agent = {router.Path} with intent {activeAgentIntent}");
                    }
                    else
                    {
                        activeAgent = null;
                        activeAgentIntent = null;

                        actorLogger.Info("Router fallback completed, active agent cleared");
                    }

                    wrapper.Context.OriginalSender.Tell(new Records.ConversationResponse(
                        routerFallbackResponse.Response,
                        wrapper.Context.Classification?.Intent,
                        wrapper.Context.Classification?.Metadata,
                        agentName,
                        routerFallbackResponse.IsCompleted,
                        routerFallbackResponse.QuickReplies));

                    break;
                }
                default:
                {
                    actorLogger.Error($"Unexpected message type: {wrapper.Response?.GetType()}");

                    wrapper.Context.OriginalSender.Tell(new Records.ConversationResponse("Unexpected internal error.", null, null, "Morgana", false));

                    break;
                }
            }

            Become(Idle);
        });

        Receive<Status.Failure>(failure =>
        {
            actorLogger.Error(failure.Cause, "Router request failed");

            ctx.OriginalSender.Tell(new Records.ConversationResponse("An internal error occurred.", null, null, "Morgana", false));

            Become(Idle);
        });

        // Re-register common handlers for FSM behavior consistency
        RegisterCommonHandlers();
    }

    /// <summary>
    /// AwaitingFollowUpResponse state: waiting for active agent to process follow-up message.
    /// Clears active agent if it signals completion.
    /// </summary>
    /// <param name="originalSender">Original sender reference for response routing</param>
    private void AwaitingFollowUpResponse(IActorRef originalSender)
    {
        actorLogger.Info("→ State: AwaitingFollowUpResponse");

        // Handle follow-up response from currently active agent (multi-turn conversation):
        // - Agent was previously set as active (IsCompleted=false in prior response)
        // - User message routed directly to this agent (bypassing guard/classifier)
        // 
        // If agent signals IsCompleted=true:
        //   - Clear activeAgent reference (multi-turn conversation ended)
        //   - Next user message will go through full flow again
        // If agent signals IsCompleted=false:
        //   - Keep activeAgent reference (conversation continues)
        //   - Next user message routes directly to same agent again
        //
        // Then return → Idle to await next user message
        ReceiveAsync<Records.FollowUpContext>(async wrapper =>
        {
            string agentName = activeAgentIntent != null ? GetAgentDisplayName(activeAgentIntent) : "Morgana";

            if (wrapper.Response.IsCompleted)
            {
                actorLogger.Info("Active agent signaled completion, clearing active agent");

                activeAgent = null;
                activeAgentIntent = null;
            }

            wrapper.OriginalSender.Tell(new Records.ConversationResponse(
                wrapper.Response.Response,
                null,
                null,
                agentName,
                wrapper.Response.IsCompleted,
                wrapper.Response.QuickReplies));

            Become(Idle);
        });

        Receive<Status.Failure>(failure =>
        {
            actorLogger.Error(failure.Cause, "Active follow-up agent did not reply");

            activeAgent = null;
            activeAgentIntent = null;
            originalSender.Tell(new Records.ConversationResponse("An internal error occurred.", null, null, "Morgana", false));

            Become(Idle);
        });

        // Re-register common handlers for FSM behavior consistency
        RegisterCommonHandlers();
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Engages the currently active agent with a follow-up message.
    /// Used when a multi-turn conversation is in progress.
    /// </summary>
    /// <param name="msg">User message to send to active agent</param>
    /// <param name="originalSender">Original sender reference for response routing</param>
    private async Task EngageActiveAgentInFollowupAsync(Records.UserMessage msg, IActorRef originalSender)
    {
        actorLogger.Info($"Follow-up detected, redirecting to active agent → {activeAgent!.Path}");

        _ = activeAgent
                .Ask<Records.AgentResponse>(
                    new Records.AgentRequest(msg.ConversationId, msg.Text, null), TimeSpan.FromSeconds(60))
                .PipeTo(
                    Self,
                    success: response => new Records.FollowUpContext(response, originalSender),
                    failure: ex => new Status.Failure(ex));

        Become(() => AwaitingFollowUpResponse(originalSender));
    }

    /// <summary>
    /// Handles unexpected failures in the Idle state.
    /// Sends error message to sender and remains in Idle state.
    /// </summary>
    /// <param name="failure">Failure information</param>
    private void HandleUnexpectedFailure(Status.Failure failure)
    {
        actorLogger.Error(failure.Cause, "Unexpected failure in ConversationSupervisorActor");

        Sender.Tell(new Records.ConversationResponse("An internal error occurred.", null, null, "Morgana", false));
    }

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

    #endregion
}