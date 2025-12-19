using Akka.Actor;
using Akka.DependencyInjection;
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
    private readonly ILogger<ConversationSupervisorActor> logger;

    private IActorRef? activeAgent = null;

    public ConversationSupervisorActor(
        string conversationId,
        ILLMService llmService,
        IPromptResolverService promptResolverService,
        ILogger<ConversationSupervisorActor> logger) : base(conversationId, llmService, promptResolverService)
    {
        this.logger = logger;

        DependencyResolver? resolver = DependencyResolver.For(Context.System);
        guard = Context.System.GetOrCreateActor<GuardActor>("guard", conversationId).GetAwaiter().GetResult();
        classifier = Context.System.GetOrCreateActor<ClassifierActor>("classifier", conversationId).GetAwaiter().GetResult();
        router = Context.System.GetOrCreateActor<RouterActor>("router", conversationId).GetAwaiter().GetResult();

        //Supervisor organizes its work as a state machine:
        //UserMessage → [activeAgent? ContinueActiveSession : InitiateNewRequest]
        //            → GuardCheckPassed
        //            → ClassificationReady
        //            → AgentResponseReceived
        //            → ConversationResponse
        ReceiveAsync<UserMessage>(msg =>
        {
            if (activeAgent is null)
                Self.Tell(new InitiateNewRequest(msg, Sender)); 
            else
                Self.Tell(new ContinueActiveSession(msg, Sender));
            return Task.CompletedTask;
        });
        ReceiveAsync<InitiateNewRequest>(EngageGuardForNewRequestAsync);
        ReceiveAsync<GuardCheckPassed>(EngageClassifierAfterGuardCheckPassedAsync);
        ReceiveAsync<ClassificationReady>(EngageRouterAfterClassificationAsync);
        ReceiveAsync<AgentResponseReceived>(DeliverAgentResponseAsync);
        ReceiveAsync<ContinueActiveSession>(EngageActiveAgentInFollowupAsync);
    }

    private async Task EngageGuardForNewRequestAsync(InitiateNewRequest msg)
    {
        try
        {
            GuardCheckResponse guardCheckResponse = await guard.Ask<GuardCheckResponse>(
                new GuardCheckRequest(msg.Message.ConversationId, msg.Message.Text));

            if (!guardCheckResponse.Compliant)
            {
                AI.Records.Prompt guardPrompt = await promptResolverService.ResolveAsync("Guard");

                msg.OriginalSender.Tell(new ConversationResponse(
                    guardPrompt.GetAdditionalProperty<string>("GuardAnswer").Replace("((violation))", guardCheckResponse.Violation),
                    "guard_violation",
                    []));

                return;
            }

            Self.Tell(new GuardCheckPassed(msg.Message, msg.OriginalSender));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Guard check failed");
            msg.OriginalSender.Tell(new ConversationResponse("Si è verificato un errore interno.", null, null));
        }
    }

    private async Task EngageClassifierAfterGuardCheckPassedAsync(GuardCheckPassed msg)
    {
        try
        {
            AI.Records.ClassificationResult classificationResult =
                await classifier.Ask<AI.Records.ClassificationResult>(msg.Message);

            logger.LogInformation("Classification result: {0}", classificationResult.Intent);

            Self.Tell(new ClassificationReady(msg.Message, classificationResult, msg.OriginalSender));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Classification failed");
            msg.OriginalSender.Tell(new ConversationResponse("Si è verificato un errore interno.", null, null));
        }
    }

    private async Task EngageRouterAfterClassificationAsync(ClassificationReady msg)
    {
        try
        {
            object agentResponse = await router.Ask<object>(
                new AI.Records.AgentRequest(msg.Message.ConversationId, msg.Message.Text, msg.Classification));

            if (agentResponse is AI.Records.InternalAgentResponse internalAgentResponse)
            {
                Self.Tell(new AgentResponseReceived(internalAgentResponse, msg.Classification, msg.OriginalSender));
            }
            else if (agentResponse is AI.Records.AgentResponse routerResponse)
            {
                logger.LogWarning("Received AgentResponse instead of InternalAgentResponse");

                if (!routerResponse.IsCompleted)
                {
                    activeAgent = router;
                    logger.LogWarning("Fallback active agent = {0}", router.Path);
                }

                msg.OriginalSender.Tell(new ConversationResponse(
                    routerResponse.Response,
                    msg.Classification.Intent,
                    msg.Classification.Metadata));
            }
            else
            {
                logger.LogError("Unexpected message type: {0}", agentResponse?.GetType());
                msg.OriginalSender.Tell(new ConversationResponse("Errore interno imprevisto.", null, null));
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Router request failed");
            msg.OriginalSender.Tell(new ConversationResponse("Si è verificato un errore interno.", null, null));
        }
    }

    private Task DeliverAgentResponseAsync(AgentResponseReceived msg)
    {
        logger.LogInformation("Received InternalAgentResponse from {0}", msg.Response.AgentRef.Path);

        if (!msg.Response.IsCompleted)
        {
            activeAgent = msg.Response.AgentRef;
            logger.LogInformation("Active agent set to {0}", activeAgent.Path);
        }

        msg.OriginalSender.Tell(new ConversationResponse(
            msg.Response.Response,
            msg.Classification.Intent,
            msg.Classification.Metadata));

        return Task.CompletedTask;
    }

    private async Task EngageActiveAgentInFollowupAsync(ContinueActiveSession msg)
    {
        logger.LogInformation("Follow-up detected, redirecting to active agent → {0}", activeAgent!.Path);

        try
        {
            AI.Records.AgentResponse agentFollowup = await activeAgent.Ask<AI.Records.AgentResponse>(
                new AI.Records.AgentRequest(msg.Message.ConversationId, msg.Message.Text, null));

            if (agentFollowup.IsCompleted)
            {
                logger.LogInformation("Active agent signaled completion → clearing active agent");
                activeAgent = null;
            }

            msg.OriginalSender.Tell(new ConversationResponse(agentFollowup.Response, null, null));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Active follow-up agent did not reply");
            activeAgent = null;
            msg.OriginalSender.Tell(new ConversationResponse("Si è verificato un errore interno.", null, null));
        }
    }
}