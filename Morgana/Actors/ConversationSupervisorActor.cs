using System.Text.Json;
using Akka.Actor;
using Akka.Event;
using Morgana.AI.Abstractions;
using Morgana.AI.Extensions;
using Morgana.AI.Interfaces;
using Morgana.Interfaces;
using static Morgana.Records;

namespace Morgana.Actors;

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

        // Handle presentation request
        ReceiveAsync<GeneratePresentationMessage>(HandlePresentationRequestAsync);
        ReceiveAsync<PresentationContext>(HandlePresentationGenerated);

        ReceiveAsync<UserMessage>(async msg =>
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
                ProcessingContext ctx = new ProcessingContext(msg, originalSender);

                guard.Ask<GuardCheckResponse>(
                    new GuardCheckRequest(msg.ConversationId, msg.Text), TimeSpan.FromSeconds(60))
                .PipeTo(Self,
                    success: response => new GuardCheckContext(response, ctx),
                    failure: ex => new Status.Failure(ex));

                Become(() => AwaitingGuardCheck(ctx));
            }
        });

        Receive<Status.Failure>(HandleUnexpectedFailure);
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
    private async Task HandlePresentationRequestAsync(GeneratePresentationMessage _)
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
            AI.Records.Prompt presentationPrompt = await promptResolverService.ResolveAsync("Presentation");

            // Load intents from domain
            List<AI.Records.IntentDefinition> allIntents = await agentConfigService.GetIntentsAsync();

            AI.Records.IntentCollection intentCollection = new AI.Records.IntentCollection(allIntents);
            List<AI.Records.IntentDefinition> displayableIntents = intentCollection.GetDisplayableIntents();

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

                hasPresented = true;
                return;
            }

            // Format intents for LLM
            string formattedIntents = string.Join("\n",
                displayableIntents.Select(i => $"- {i.Name}: {i.Description}"));

            // Build LLM prompt
            string systemPrompt = $"{presentationPrompt.Content}\n\n{presentationPrompt.Instructions}"
                .Replace("((intents))", formattedIntents);

            actorLogger.Info("Calling LLM for presentation generation");

            // Call LLM to generate structured JSON
            string llmResponse = await llmService.CompleteWithSystemPromptAsync(
                conversationId,
                systemPrompt,
                "Generate the presentation message");

            actorLogger.Info($"LLM raw response: {llmResponse}");

            // Parse LLM response
            PresentationResponse? presentationResponse =
                JsonSerializer.Deserialize<PresentationResponse>(llmResponse);

            if (presentationResponse == null)
            {
                throw new InvalidOperationException("LLM returned null presentation response");
            }

            actorLogger.Info($"LLM generated presentation with {presentationResponse.QuickReplies.Count} quick replies");

            // Convert to internal format
            Self.Tell(new PresentationContext(presentationResponse.Message, displayableIntents)
            {
                LlmQuickReplies = presentationResponse.QuickReplies
            });
        }
        catch (Exception ex)
        {
            actorLogger.Error(ex, "LLM presentation generation failed, using fallback");

            // Fallback: Generate from intents directly
            AI.Records.Prompt presentationPrompt = await promptResolverService.ResolveAsync("Presentation");

            string fallbackMessage = presentationPrompt.GetAdditionalProperty<string>("FallbackMessage");

            // Build fallback quick replies from intents
            List<AI.Records.IntentDefinition> allIntents = await agentConfigService.GetIntentsAsync();

            AI.Records.IntentCollection intentCollection = new AI.Records.IntentCollection(allIntents);
            List<AI.Records.IntentDefinition> displayableIntents = intentCollection.GetDisplayableIntents();

            List<AI.Records.QuickReply> fallbackReplies = displayableIntents
                .Select(intent => new AI.Records.QuickReply(
                    intent.Name,
                    intent.Label ?? intent.Name,
                    intent.DefaultValue ?? $"Help me with {intent.Name}"))
                .ToList();

            actorLogger.Info($"Using fallback presentation with {fallbackReplies.Count} quick replies");

            Self.Tell(new PresentationContext(fallbackMessage, displayableIntents)
            {
                LlmQuickReplies = fallbackReplies
            });
        }
    }

    /// <summary>
    /// Handles the generated presentation and sends it to the client via SignalR.
    /// </summary>
    /// <param name="ctx">Context containing the presentation message and quick replies</param>
    private async Task HandlePresentationGenerated(PresentationContext ctx)
    {
        actorLogger.Info("Sending presentation to client via SignalR");

        // Convert LLM-generated quick replies to SignalR format
        List<AI.Records.QuickReply> quickReplies = ctx.LlmQuickReplies?
            .Select(qr => new AI.Records.QuickReply(qr.Id, qr.Label, qr.Value))
            .ToList() ?? [];

        try
        {
            await Task.Delay(700); // Give SignalR time to join conversation

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
    private void AwaitingGuardCheck(ProcessingContext ctx)
    {
        actorLogger.Info("→ State: AwaitingGuardCheck");

        ReceiveAsync<GuardCheckContext>(async wrapper =>
        {
            if (!wrapper.Response.Compliant)
            {
                actorLogger.Warning($"Message rejected by guard: {wrapper.Response.Violation}");

                AI.Records.Prompt guardPrompt = await promptResolverService.ResolveAsync("Guard");
                string guardAnswer = guardPrompt.GetAdditionalProperty<string>("GuardAnswer")
                    .Replace("((violation))", wrapper.Response.Violation ?? "Content policy violation");

                wrapper.Context.OriginalSender.Tell(new ConversationResponse(
                    guardAnswer, null, null, "Morgana", false));

                Become(Idle);
                return;
            }

            actorLogger.Info("Message passed guard check, proceeding to classification");

            classifier.Ask<AI.Records.ClassificationResult>(
                wrapper.Context.OriginalMessage, TimeSpan.FromSeconds(60))
            .PipeTo(Self,
                success: result => new ClassificationContext(result, wrapper.Context),
                failure: ex => new Status.Failure(ex));

            Become(() => AwaitingClassification(ctx));
        });

        Receive<Status.Failure>(failure =>
        {
            actorLogger.Error(failure.Cause, "Guard check failed");
            ctx.OriginalSender.Tell(new ConversationResponse("An internal error occurred.", null, null, "Morgana", false));
            Become(Idle);
        });
    }

    /// <summary>
    /// AwaitingClassification state: waiting for intent classification result from ClassifierActor.
    /// On success: forwards to RouterActor and transitions to AwaitingAgentResponse.
    /// On failure: sends error message to client and returns to Idle.
    /// </summary>
    /// <param name="ctx">Processing context containing original message and sender</param>
    private void AwaitingClassification(ProcessingContext ctx)
    {
        actorLogger.Info("→ State: AwaitingClassification");

        ReceiveAsync<ClassificationContext>(async wrapper =>
        {
            actorLogger.Info($"Classification result: {wrapper.Classification.Intent}");

            ProcessingContext updatedCtx = ctx with { Classification = wrapper.Classification };

            router.Ask<object>(
                new AI.Records.AgentRequest(
                    wrapper.Context.OriginalMessage.ConversationId,
                    wrapper.Context.OriginalMessage.Text,
                    wrapper.Classification), TimeSpan.FromSeconds(60))
            .PipeTo(Self,
                success: response => new AgentContext(response, updatedCtx),
                failure: ex => new Status.Failure(ex));

            Become(() => AwaitingAgentResponse(updatedCtx));
        });

        Receive<Status.Failure>(failure =>
        {
            actorLogger.Error(failure.Cause, "Classification failed");
            ctx.OriginalSender.Tell(new ConversationResponse("An internal error occurred.", null, null, "Morgana", false));

            Become(Idle);
        });
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
    private void AwaitingAgentResponse(ProcessingContext ctx)
    {
        actorLogger.Info("→ State: AwaitingAgentResponse");

        ReceiveAsync<AgentContext>(async wrapper =>
        {
            string agentName = GetAgentDisplayName(wrapper.Response, ctx.Classification?.Intent);

            switch (wrapper.Response)
            {
                case AI.Records.ActiveAgentResponse activeAgentResponse:
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

                    wrapper.Context.OriginalSender.Tell(new ConversationResponse(
                        activeAgentResponse.Response,
                        wrapper.Context.Classification?.Intent,
                        wrapper.Context.Classification?.Metadata,
                        agentName,
                        activeAgentResponse.IsCompleted,
                        activeAgentResponse.QuickReplies));

                    break;
                }
                case AI.Records.AgentResponse routerFallbackResponse:
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

                    wrapper.Context.OriginalSender.Tell(new ConversationResponse(
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
                    wrapper.Context.OriginalSender.Tell(new ConversationResponse("Unexpected internal error.", null, null, "Morgana", false));
                    break;
                }
            }

            Become(Idle);
        });

        Receive<Status.Failure>(failure =>
        {
            actorLogger.Error(failure.Cause, "Router request failed");
            ctx.OriginalSender.Tell(new ConversationResponse("An internal error occurred.", null, null, "Morgana", false));
            Become(Idle);
        });
    }

    /// <summary>
    /// AwaitingFollowUpResponse state: waiting for active agent to process follow-up message.
    /// Clears active agent if it signals completion.
    /// </summary>
    /// <param name="originalSender">Original sender reference for response routing</param>
    private void AwaitingFollowUpResponse(IActorRef originalSender)
    {
        actorLogger.Info("→ State: AwaitingFollowUpResponse");

        ReceiveAsync<FollowUpContext>(async wrapper =>
        {
            string agentName = activeAgentIntent != null
                ? GetAgentDisplayName(null, activeAgentIntent)
                : "Morgana";

            bool agentCompleted = false;

            if (wrapper.Response.IsCompleted)
            {
                actorLogger.Info("Active agent signaled completion, clearing active agent");

                // Mark completion only if it was a specialized agent
                agentCompleted = activeAgentIntent != null;

                activeAgent = null;
                activeAgentIntent = null;
            }

            wrapper.OriginalSender.Tell(new ConversationResponse(
                wrapper.Response.Response,
                null,
                null,
                agentName,
                agentCompleted,
                wrapper.Response.QuickReplies));

            Become(Idle);
        });

        Receive<Status.Failure>(failure =>
        {
            actorLogger.Error(failure.Cause, "Active follow-up agent did not reply");
            activeAgent = null;
            activeAgentIntent = null;
            originalSender.Tell(new ConversationResponse("An internal error occurred.", null, null, "Morgana", false));
            Become(Idle);
        });
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Engages the currently active agent with a follow-up message.
    /// Used when a multi-turn conversation is in progress.
    /// </summary>
    /// <param name="msg">User message to send to active agent</param>
    /// <param name="originalSender">Original sender reference for response routing</param>
    private async Task EngageActiveAgentInFollowupAsync(UserMessage msg, IActorRef originalSender)
    {
        actorLogger.Info($"Follow-up detected, redirecting to active agent → {activeAgent!.Path}");

        activeAgent.Ask<AI.Records.AgentResponse>(
            new AI.Records.AgentRequest(msg.ConversationId, msg.Text, null), TimeSpan.FromSeconds(60))
        .PipeTo(Self,
            success: response => new FollowUpContext(response, originalSender),
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
        Sender.Tell(new ConversationResponse("An internal error occurred.", null, null, "Morgana", false));
    }

    /// <summary>
    /// Gets the display name for an agent based on its intent.
    /// Returns "Morgana" for the "other" intent or missing intents.
    /// Returns "Morgana (Intent)" for specialized intents.
    /// </summary>
    /// <param name="response">Optional response object (unused currently)</param>
    /// <param name="intent">Intent name to format</param>
    /// <returns>Formatted agent display name</returns>
    private string GetAgentDisplayName(object? response, string? intent)
    {
        if (string.IsNullOrEmpty(intent) || string.Equals(intent, "other", StringComparison.OrdinalIgnoreCase))
            return "Morgana";

        // Capitalize first letter of intent for display
        string capitalizedIntent = char.ToUpper(intent[0]) + intent[1..];

        return $"Morgana ({capitalizedIntent})";
    }

    #endregion
}