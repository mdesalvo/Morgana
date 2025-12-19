using Akka.Actor;
using Akka.DependencyInjection;
using Morgana.AI.Abstractions;
using Morgana.AI.Extensions;
using Morgana.AI.Interfaces;

namespace Morgana.Actors;

public class ConversationSupervisorActor : MorganaActor
{
    private readonly IActorRef guard;
    private readonly IActorRef classifier;
    private readonly IActorRef router;
    private readonly ILogger<ConversationSupervisorActor> logger;

    // Eventuale agente ancora attivo in multi-turno
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

        ReceiveAsync<Records.UserMessage>(HandleUserMessageAsync);
    }

    private async Task HandleUserMessageAsync(Records.UserMessage msg)
    {
        IActorRef? senderRef = Sender;

        // If we have an active agent still serving its use-case, bypass classifier and router
        if (activeAgent != null)
        {
            logger.LogInformation("Supervisor: follow-up detected, redirecting to active agent → {0}", activeAgent.Path);

            AI.Records.AgentResponse agentFollowup;
            try
            {
                agentFollowup = await activeAgent.Ask<AI.Records.AgentResponse>(
                    new AI.Records.AgentRequest(msg.ConversationId, msg.Text, null));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Supervisor: Active follow-up agent did not reply.");
                activeAgent = null;

                senderRef.Tell(new Records.ConversationResponse("Si è verificato un errore interno.", null, null));
                return;
            }

            if (agentFollowup.IsCompleted)
            {
                logger.LogInformation("Supervisor: Active agent signaled completion → clearing active agent.");
                activeAgent = null;
            }

            senderRef.Tell(new Records.ConversationResponse(agentFollowup.Response, null, null));
            return;
        }

        // Otherwise we can start a new service request: guard → classifier → router → agent+LLM
        try
        {
            Records.GuardCheckResponse? guardCheckResponse = await guard.Ask<Records.GuardCheckResponse>(
                new Records.GuardCheckRequest(msg.ConversationId, msg.Text));
            if (!guardCheckResponse.Compliant)
            {
                AI.Records.Prompt guardPrompt = await promptResolverService.ResolveAsync("Guard");
                Records.ConversationResponse response = new Records.ConversationResponse(
                    guardPrompt.GetAdditionalProperty<string>("GuardAnswer").Replace("((violation))", guardCheckResponse.Violation), "guard_violation", []);

                senderRef.Tell(response);
                return;
            }

            AI.Records.ClassificationResult? classificationResult = await classifier.Ask<AI.Records.ClassificationResult>(msg);
            logger.LogInformation("Supervisor: got classification result {0}", classificationResult.Intent);

            // Risposta polimorfica: può essere InternalAgentResponse o AgentResponse
            object? agentResponse = await router.Ask<object>(
                new AI.Records.AgentRequest(msg.ConversationId, msg.Text, classificationResult));
            switch (agentResponse)
            {
                // Caso 1: InternalAgentResponse → proveniente dall'agente
                case AI.Records.InternalAgentResponse internalAgentResponse:
                    {
                        logger.LogInformation("Supervisor: received InternalAgentResponse from {0}", internalAgentResponse.AgentRef.Path);

                        // Se multi-turno → imposta agente attivo
                        if (!internalAgentResponse.IsCompleted)
                        {
                            activeAgent = internalAgentResponse.AgentRef;
                            logger.LogInformation("Supervisor: active agent set to {0}", activeAgent.Path);
                        }

                        senderRef.Tell(new Records.ConversationResponse(
                            internalAgentResponse.Response,
                            classificationResult.Intent,
                            classificationResult.Metadata));

                        break;
                    }
                // Caso 2: AgentResponse → proveniente dal router
                case AI.Records.AgentResponse routerResponse:
                    {
                        logger.LogWarning("Supervisor: received AgentResponse instead of InternalAgentResponse");

                        // Se multi-turno → imposta agente attivo = router (non ideale, ma evita blocchi)
                        if (!routerResponse.IsCompleted)
                        {
                            activeAgent = router;
                            logger.LogWarning("Supervisor: fallback active agent = {0}", router.Path);
                        }

                        senderRef.Tell(new Records.ConversationResponse(
                            routerResponse.Response,
                            classificationResult.Intent,
                            classificationResult.Metadata));

                        break;
                    }
                // Caso 3: risposta inattesa
                default:
                    logger.LogError("Supervisor: received unexpected message type: {0}", agentResponse?.GetType());

                    senderRef.Tell(new Records.ConversationResponse("Errore interno imprevisto.", null, null));
                    break;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Errore nel gestire il primo turno.");
            senderRef.Tell(new Records.ConversationResponse("Si è verificato un errore interno.", null, null));
        }
    }
}