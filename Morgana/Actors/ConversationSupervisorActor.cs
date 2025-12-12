using Akka.Actor;
using Akka.DependencyInjection;
using Morgana;
using Morgana.Actors;

public class ConversationSupervisorActor : MorganaActor
{
    private readonly IActorRef guardian;
    private readonly IActorRef classifier;
    private readonly IActorRef router;
    private readonly ILogger<ConversationSupervisorActor> logger;

    // Agente attivo in multi-turno (BillingAgent, HardwareTroubleshootingAgent, ecc.)
    private IActorRef? activeAgent = null;

    public ConversationSupervisorActor(string conversationId, ILogger<ConversationSupervisorActor> logger) : base(conversationId)
    {
        this.logger = logger;

        DependencyResolver? resolver = DependencyResolver.For(Context.System);

        guardian = Context.ActorOf(
            resolver.Props<Morgana.Actors.GuardianActor>(conversationId),
            $"guardian-{conversationId}");

        classifier = Context.ActorOf(
            resolver.Props<ClassifierActor>(conversationId),
            $"classifier-{conversationId}");

        router = Context.ActorOf(
            resolver.Props<RouterActor>(conversationId),
            $"router-{conversationId}");

        ReceiveAsync<Records.UserMessage>(HandleUserMessageAsync);
    }

    private async Task HandleUserMessageAsync(Records.UserMessage msg)
    {
        IActorRef? senderRef = Sender;

        // 🔥 1) SE ABBIAMO UN AGENTE ATTIVO → bypassa classifier e agenti intermedi
        if (activeAgent != null)
        {
            logger.LogInformation("Supervisor: follow-up detected, redirecting to active agent → {0}", activeAgent.Path);

            Records.AgentResponse agentFollowup;
            try
            {
                agentFollowup = await activeAgent.Ask<Records.AgentResponse>(
                    new Records.ExecuteRequest(msg.ConversationId, msg.Text, null));
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

        // 🔥 2) PRIMO TURNO: Guardia → Classificazione → Router → Agente concreto
        try
        {
            Records.GuardCheckResponse? guardCheckResponse = await guardian.Ask<Records.GuardCheckResponse>(
                new Records.GuardCheckRequest(msg.ConversationId, msg.Text));
            if (!guardCheckResponse.Compliant)
            {
                Records.ConversationResponse response = new Records.ConversationResponse(
                    $"La prego di mantenere un tono professionale. {guardCheckResponse.Violation}",
                    "guard_violation",
                    []);
                senderRef.Tell(response);
                return;
            }

            Records.ClassificationResult? classificationResult = await classifier.Ask<Records.ClassificationResult>(msg);
            logger.LogInformation("Supervisor got classification result: {0}", classificationResult.Intent);

            // Risposta polimorfica: può essere InternalAgentResponse o AgentResponse
            object? agentResponse = await router.Ask<object>(
                new Records.ExecuteRequest(msg.ConversationId, msg.Text, classificationResult));

            // Caso 1: InternalAgentResponse → proveniente dall'agente di routing
            if (agentResponse is Records.InternalAgentResponse internalAgentResponse)
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

                return;
            }

            // Caso 2: AgentResponse diretto (fallback, dovrebbe essere raro)
            if (agentResponse is Records.AgentResponse directAgentResponse)
            {
                logger.LogWarning("Supervisor received direct AgentResponse instead of internal one.");

                if (!directAgentResponse.IsCompleted)
                {
                    // fallback: usa direttamente l'attore di routing come agente attivo (non ideale, ma evita blocchi)
                    activeAgent = router;
                    logger.LogWarning("Supervisor: fallback active agent = {0}", router.Path);
                }

                senderRef.Tell(new Records.ConversationResponse(
                    directAgentResponse.Response,
                    classificationResult.Intent,
                    classificationResult.Metadata));

                return;
            }

            // Caso 3: risposta inattesa
            logger.LogError("Supervisor received unexpected message type: {0}", agentResponse?.GetType());
            senderRef.Tell(new Records.ConversationResponse("Errore interno imprevisto.", null, null));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Errore nel gestire il primo turno.");
            senderRef.Tell(new Records.ConversationResponse("Si è verificato un errore interno.", null, null));
        }
    }
}