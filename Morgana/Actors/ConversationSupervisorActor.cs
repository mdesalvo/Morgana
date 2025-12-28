using Akka.Actor;
using Akka.Event;
using Morgana.AI.Abstractions;
using Morgana.AI.Extensions;
using Morgana.AI.Interfaces;
using static Morgana.Records;

namespace Morgana.Actors;

public class ConversationSupervisorActor : MorganaActor
{
    private readonly IActorRef guard;
    private readonly IActorRef classifier;
    private readonly IActorRef router;

    private IActorRef? activeAgent = null;

    public ConversationSupervisorActor(
        string conversationId,
        ILLMService llmService,
        IPromptResolverService promptResolverService,
        ILogger<ConversationSupervisorActor> _) : base(conversationId, llmService, promptResolverService)
    {
        guard = Context.System.GetOrCreateActor<GuardActor>("guard", conversationId).GetAwaiter().GetResult();
        classifier = Context.System.GetOrCreateActor<ClassifierActor>("classifier", conversationId).GetAwaiter().GetResult();
        router = Context.System.GetOrCreateActor<RouterActor>("router", conversationId).GetAwaiter().GetResult();

        Idle();
    }

    #region State Behaviors

    private void Idle()
    {
        actorLogger.Info("→ State: Idle");

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
                    new GuardCheckRequest(msg.ConversationId, msg.Text),
                    TimeSpan.FromSeconds(60))
                .PipeTo(Self,
                    success: response => new GuardCheckContext(response, ctx),
                    failure: ex => new Status.Failure(ex));

                Become(() => AwaitingGuardCheck(ctx));
            }
        });

        Receive<Status.Failure>(HandleUnexpectedFailure);
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
                        .Replace("((violation))", wrapper.Response.Violation),
                    "guard_violation",
                    []));

                Become(Idle);
                return;
            }

            actorLogger.Info("Guard check passed, proceeding to classification");

            classifier.Ask<AI.Records.ClassificationResult>(
                wrapper.Context.OriginalMessage,
                TimeSpan.FromSeconds(60))
            .PipeTo(Self,
                success: result => new ClassificationContext(result, wrapper.Context),
                failure: ex => new Status.Failure(ex));

            Become(() => AwaitingClassification(wrapper.Context));
        });

        Receive<Status.Failure>(failure =>
        {
            actorLogger.Error(failure.Cause, "Guard check failed");
            ctx.OriginalSender.Tell(new ConversationResponse(
                "Si è verificato un errore interno.", null, null));
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
                    wrapper.Classification),
                TimeSpan.FromSeconds(60))
            .PipeTo(Self,
                success: response => new AgentContext(response, updatedCtx),
                failure: ex => new Status.Failure(ex));

            Become(() => AwaitingAgentResponse(updatedCtx));
        });

        Receive<Status.Failure>(failure =>
        {
            actorLogger.Error(failure.Cause, "Classification failed");
            ctx.OriginalSender.Tell(new ConversationResponse(
                "Si è verificato un errore interno.", null, null));
            Become(Idle);
        });
    }

    private void AwaitingAgentResponse(ProcessingContext ctx)
    {
        actorLogger.Info("→ State: AwaitingAgentResponse");

        ReceiveAsync<AgentContext>(async wrapper =>
        {
            if (wrapper.Response is AI.Records.ActiveAgentResponse activeAgentResponse)
            {
                actorLogger.Info($"Received ActiveAgentResponse from {activeAgentResponse.AgentRef.Path}, completed: {activeAgentResponse.IsCompleted}");

                if (!activeAgentResponse.IsCompleted)
                {
                    activeAgent = activeAgentResponse.AgentRef;
                    actorLogger.Info($"Active agent set to {activeAgent.Path}");
                }
                else
                {
                    activeAgent = null;
                }

                wrapper.Context.OriginalSender.Tell(new ConversationResponse(
                    activeAgentResponse.Response,
                    wrapper.Context.Classification?.Intent,
                    wrapper.Context.Classification?.Metadata));

                Become(Idle);
            }
            else if (wrapper.Response is AI.Records.AgentResponse routerFallbackResponse)
            {
                actorLogger.Warning("Received AgentResponse instead of ActiveAgentResponse (router fallback)");

                if (!routerFallbackResponse.IsCompleted)
                {
                    activeAgent = router;
                    actorLogger.Warning($"Fallback active agent = {router.Path}");
                }

                wrapper.Context.OriginalSender.Tell(new ConversationResponse(
                    routerFallbackResponse.Response,
                    wrapper.Context.Classification?.Intent,
                    wrapper.Context.Classification?.Metadata));

                Become(Idle);
            }
            else
            {
                actorLogger.Error($"Unexpected message type: {wrapper.Response?.GetType()}");
                wrapper.Context.OriginalSender.Tell(new ConversationResponse(
                    "Errore interno imprevisto.", null, null));
                Become(Idle);
            }
        });

        Receive<Status.Failure>(failure =>
        {
            actorLogger.Error(failure.Cause, "Router request failed");
            ctx.OriginalSender.Tell(new ConversationResponse(
                "Si è verificato un errore interno.", null, null));
            Become(Idle);
        });
    }

    private void AwaitingFollowUpResponse(IActorRef originalSender)
    {
        actorLogger.Info("→ State: AwaitingFollowUpResponse");

        ReceiveAsync<FollowUpContext>(async wrapper =>
        {
            if (wrapper.Response.IsCompleted)
            {
                actorLogger.Info("Active agent signaled completion, clearing active agent");
                activeAgent = null;
            }

            wrapper.OriginalSender.Tell(new ConversationResponse(
                wrapper.Response.Response, null, null));

            Become(Idle);
        });

        Receive<Status.Failure>(failure =>
        {
            actorLogger.Error(failure.Cause, "Active follow-up agent did not reply");
            activeAgent = null;
            originalSender.Tell(new ConversationResponse(
                "Si è verificato un errore interno.", null, null));
            Become(Idle);
        });
    }

    #endregion

    #region Helper Methods

    private async Task EngageActiveAgentInFollowupAsync(UserMessage msg, IActorRef originalSender)
    {
        actorLogger.Info($"Follow-up detected, redirecting to active agent → {activeAgent!.Path}");

        activeAgent.Ask<AI.Records.AgentResponse>(
            new AI.Records.AgentRequest(msg.ConversationId, msg.Text, null),
            TimeSpan.FromSeconds(60))
        .PipeTo(Self,
            success: response => new FollowUpContext(response, originalSender),
            failure: ex => new Status.Failure(ex));

        Become(() => AwaitingFollowUpResponse(originalSender));
    }

    private void HandleUnexpectedFailure(Status.Failure failure)
    {
        actorLogger.Error(failure.Cause, "Unexpected failure in ConversationSupervisorActor");
        Sender.Tell(new ConversationResponse(
            "Si è verificato un errore interno.", null, null));
    }

    #endregion
}