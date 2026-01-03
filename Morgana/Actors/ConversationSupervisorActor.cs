using System.Text.Json;
using Akka.Actor;
using Akka.Event;
using Morgana.AI.Abstractions;
using Morgana.AI.Attributes;
using Morgana.AI.Extensions;
using Morgana.AI.Interfaces;
using Morgana.Interfaces;
using static Morgana.Records;

namespace Morgana.Actors;

public class ConversationSupervisorActor : MorganaActor
{
    private readonly IActorRef guard;
    private readonly IActorRef classifier;
    private readonly IActorRef router;
    private readonly ISignalRBridgeService signalRBridgeService;
    private readonly IAgentConfigurationService agentConfigService;

    private IActorRef? activeAgent;
    private string? activeAgentIntent;
    private bool hasPresented = false;

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
                "Genera il messaggio di presentazione");

            actorLogger.Info($"LLM raw response: {llmResponse}");

            // Parse LLM response
            AI.Records.PresentationResponse? presentationResponse = 
                JsonSerializer.Deserialize<AI.Records.PresentationResponse>(llmResponse);

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
            
            List<AI.Records.QuickReplyDefinition> fallbackReplies = displayableIntents
                .Select(intent => new AI.Records.QuickReplyDefinition(
                    intent.Name,
                    intent.Label ?? intent.Name,
                    intent.DefaultValue ?? $"Aiutami con {intent.Name}"))
                .ToList();

            actorLogger.Info($"Using fallback presentation with {fallbackReplies.Count} quick replies");

            Self.Tell(new PresentationContext(fallbackMessage, displayableIntents)
            {
                LlmQuickReplies = fallbackReplies
            });
        }
    }

    private async Task HandlePresentationGenerated(PresentationContext ctx)
    {
        actorLogger.Info("Sending presentation to client via SignalR");

        // Convert LLM-generated quick replies to SignalR format
        List<QuickReply> quickReplies = ctx.LlmQuickReplies?
            .Select(qr => new QuickReply(qr.Id, qr.Label, qr.Value))
            .ToList() ?? [];

        try
        {
            await signalRBridgeService.SendStructuredMessageAsync(
                conversationId,
                ctx.Message,
                "presentation",
                quickReplies,
                null,
                "Morgana");

            actorLogger.Info($"Presentation sent successfully with {quickReplies.Count} quick replies");
        }
        catch (Exception ex)
        {
            actorLogger.Error(ex, "Failed to send presentation via SignalR");
        }
    }

    private void AwaitingGuardCheck(ProcessingContext ctx)
    {
        actorLogger.Info("→ State: AwaitingGuardCheck");

        ReceiveAsync<GuardCheckContext>(async wrapper =>
        {
            if (!wrapper.Response.Compliant)
            {
                actorLogger.Warning($"Guard violation: {wrapper.Response.Violation}");

                AI.Records.Prompt guardPrompt = await promptResolverService.ResolveAsync("Guard");
                wrapper.Context.OriginalSender.Tell(new ConversationResponse(
                    guardPrompt.GetAdditionalProperty<string>("GuardAnswer")
                        .Replace("((violation))", wrapper.Response.Violation ?? "Contenuto non appropriato"),
                    "guard_violation",
                    [],
                    "Morgana"));

                Become(Idle);
                return;
            }

            actorLogger.Info("Guard check passed, proceeding to classification");

            classifier.Ask<AI.Records.ClassificationResult>(
                wrapper.Context.OriginalMessage, TimeSpan.FromSeconds(60))
            .PipeTo(Self,
                success: result => new ClassificationContext(result, wrapper.Context),
                failure: ex => new Status.Failure(ex));

            Become(() => AwaitingClassification(wrapper.Context));
        });

        Receive<Status.Failure>(failure =>
        {
            actorLogger.Error(failure.Cause, "Guard check failed");
            ctx.OriginalSender.Tell(new ConversationResponse("Si è verificato un errore interno.", null, null, "Morgana"));
            Become(Idle);
        });
    }

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
            ctx.OriginalSender.Tell(new ConversationResponse("Si è verificato un errore interno.", null, null, "Morgana"));

            Become(Idle);
        });
    }

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
                    actorLogger.Info($"Received ActiveAgentResponse from {activeAgentResponse.AgentRef.Path}, completed: {activeAgentResponse.IsCompleted}");

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
                    }

                    wrapper.Context.OriginalSender.Tell(new ConversationResponse(
                        activeAgentResponse.Response,
                        wrapper.Context.Classification?.Intent,
                        wrapper.Context.Classification?.Metadata,
                        agentName));

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

                    wrapper.Context.OriginalSender.Tell(new ConversationResponse(
                        routerFallbackResponse.Response,
                        wrapper.Context.Classification?.Intent,
                        wrapper.Context.Classification?.Metadata,
                        agentName));

                    break;
                }
                default:
                    actorLogger.Error($"Unexpected message type: {wrapper.Response?.GetType()}");
                    wrapper.Context.OriginalSender.Tell(new ConversationResponse("Errore interno imprevisto.", null, null, "Morgana"));
                    break;
            }

            Become(Idle);
        });

        Receive<Status.Failure>(failure =>
        {
            actorLogger.Error(failure.Cause, "Router request failed");
            ctx.OriginalSender.Tell(new ConversationResponse("Si è verificato un errore interno.", null, null, "Morgana"));
            Become(Idle);
        });
    }

    private void AwaitingFollowUpResponse(IActorRef originalSender)
    {
        actorLogger.Info("→ State: AwaitingFollowUpResponse");

        ReceiveAsync<FollowUpContext>(async wrapper =>
        {
            string agentName = activeAgentIntent != null 
                ? GetAgentDisplayName(null, activeAgentIntent)
                : "Morgana";

            if (wrapper.Response.IsCompleted)
            {
                actorLogger.Info("Active agent signaled completion, clearing active agent");
                activeAgent = null;
                activeAgentIntent = null;
            }

            wrapper.OriginalSender.Tell(new ConversationResponse(
                wrapper.Response.Response, 
                null, 
                null,
                agentName));

            Become(Idle);
        });

        Receive<Status.Failure>(failure =>
        {
            actorLogger.Error(failure.Cause, "Active follow-up agent did not reply");
            activeAgent = null;
            activeAgentIntent = null;
            originalSender.Tell(new ConversationResponse("Si è verificato un errore interno.", null, null, "Morgana"));
            Become(Idle);
        });
    }

    #endregion

    #region Helper Methods

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

    private void HandleUnexpectedFailure(Status.Failure failure)
    {
        actorLogger.Error(failure.Cause, "Unexpected failure in ConversationSupervisorActor");
        Sender.Tell(new ConversationResponse("Si è verificato un errore interno.", null, null, "Morgana"));
    }

    private string GetAgentDisplayName(object? response, string? intent)
    {
        if (string.IsNullOrEmpty(intent))
            return "Morgana";

        // Capitalize first letter of intent for display
        string capitalizedIntent = char.ToUpper(intent[0]) + intent.Substring(1);
        
        return $"Morgana ({capitalizedIntent})";
    }

    #endregion
}