using Akka.Actor;
using Akka.DependencyInjection;
using Morgana.AI.Abstractions;
using Morgana.AI.Interfaces;

namespace Morgana.Actors;

public class ConversationSupervisorActor : MorganaActor
{
    private readonly IActorRef guardActor;
    private readonly IActorRef classifierActor;
    private readonly IActorRef routerActor;
    private readonly IPromptResolverService promptResolverService;
    private readonly ILogger<ConversationSupervisorActor> logger;

    // Eventuale agente ancora attivo in multi-turno
    private IActorRef? activeAgent = null;

    public ConversationSupervisorActor(
        string conversationId,
        IPromptResolverService promptResolverService,
        ILogger<ConversationSupervisorActor> logger) : base(conversationId)
    {
        this.promptResolverService = promptResolverService;
        this.logger = logger;

        DependencyResolver? resolver = DependencyResolver.For(Context.System);

        guardActor = Context.ActorOf(resolver.Props<GuardActor>(conversationId, promptResolverService), $"guard-{conversationId}");
        classifierActor = Context.ActorOf(resolver.Props<ClassifierActor>(conversationId, promptResolverService), $"classifier-{conversationId}");
        routerActor = Context.ActorOf(resolver.Props<RouterActor>(conversationId, promptResolverService), $"router-{conversationId}");

        ReceiveAsync<Records.UserMessage>(HandleUserMessageAsync);
    }

    private async Task HandleUserMessageAsync(Records.UserMessage msg)
    {
        IActorRef? senderRef = Sender;

        // If we have an active agent still serving its use-case, bypass classifier and router
        if (activeAgent != null)
        {
            logger.LogInformation("Supervisor: follow-up detected, redirecting to active agent → {0}", activeAgent.Path);

            Morgana.AI.Records.AgentResponse agentFollowup;
            try
            {
                agentFollowup = await activeAgent.Ask<Morgana.AI.Records.AgentResponse>(
                    new Morgana.AI.Records.AgentRequest(msg.ConversationId, msg.Text, null));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Active follow-up agent did not reply.");
                activeAgent = null;

                senderRef.Tell(new Records.ConversationResponse("Si è verificato un errore interno.", null, null));
                return;
            }

            if (agentFollowup.IsCompleted)
            {
                logger.LogInformation("Agent signaled completion → clearing active agent.");
                activeAgent = null;
            }

            senderRef.Tell(new Records.ConversationResponse(agentFollowup.Response, null, null));
            return;
        }

        // Otherwise we can start a new service request: guard → classifier → router → agent+LLM
        try
        {
            Records.GuardCheckResponse? guardCheckResponse = await guardActor.Ask<Records.GuardCheckResponse>(
                new Records.GuardCheckRequest(msg.ConversationId, msg.Text));
            if (!guardCheckResponse.Compliant)
            {
                AI.Records.Prompt guardPrompt = await promptResolverService.ResolveAsync("Guard");
                Records.ConversationResponse response = new Records.ConversationResponse(
                    ((string)guardPrompt.AdditionalProperties["GuardAnswer"]).Replace("{{violation}}", guardCheckResponse.Violation), "guard_violation", []);

                senderRef.Tell(response);
                return;
            }

            Morgana.AI.Records.ClassificationResult? classificationResult = await classifierActor.Ask<Morgana.AI.Records.ClassificationResult>(msg);
            logger.LogInformation("Supervisor got classification result: {0}", classificationResult.Intent);

            // Risposta polimorfica: può essere InternalAgentResponse o AgentResponse
            object? agentResponse = await routerActor.Ask<object>(
                new Morgana.AI.Records.AgentRequest(msg.ConversationId, msg.Text, classificationResult));
            switch (agentResponse)
            {
                // Caso 1: InternalAgentResponse → proveniente dall'agente di routing
                case Morgana.AI.Records.InternalAgentResponse internalAgentResponse:
                {
                    logger.LogInformation("Supervisor received InternalAgentResponse from {0}", internalAgentResponse.AgentRef.Path);

                    // Se multi-turno → imposta agente concreto
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
                // Caso 2: AgentResponse diretto (fallback, dovrebbe essere raro)
                case Morgana.AI.Records.AgentResponse directAgentResponse:
                {
                    logger.LogWarning("Supervisor received direct AgentResponse instead of internal one.");

                    if (!directAgentResponse.IsCompleted)
                    {
                        // fallback: usa direttamente l'attore di routing come agente attivo (non ideale, ma evita blocchi)
                        activeAgent = routerActor;
                        logger.LogWarning("Supervisor: fallback active agent = {0}", routerActor.Path);
                    }

                    senderRef.Tell(new Records.ConversationResponse(
                        directAgentResponse.Response,
                        classificationResult.Intent,
                        classificationResult.Metadata));

                    break;
                }
                default:
                    // Caso 3: risposta inattesa
                    logger.LogError("Supervisor received unexpected message type: {0}", agentResponse?.GetType());

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