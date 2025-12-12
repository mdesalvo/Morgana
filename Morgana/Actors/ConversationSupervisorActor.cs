using Akka.Actor;
using Akka.DependencyInjection;
using Morgana;
using Morgana.Actors;

public class ConversationSupervisorActor : MorganaActor
{
    private readonly IActorRef classifierActor;
    private readonly IActorRef guardianActor;
    private readonly IActorRef informativeActor;
    private readonly IActorRef dispositiveActor;
    private readonly ILogger<ConversationSupervisorActor> logger;

    // Agente attivo in multi-turno (BillingAgent, HardwareTroubleShootingAgent, ecc.)
    private IActorRef? activeAgent = null;

    public ConversationSupervisorActor(string conversationId, ILogger<ConversationSupervisorActor> logger) : base(conversationId)
    {
        this.logger = logger;

        DependencyResolver? resolver = DependencyResolver.For(Context.System);

        guardianActor = Context.ActorOf(
            resolver.Props<Morgana.Actors.GuardianActor>(conversationId),
            $"guardian-{conversationId}");

        classifierActor = Context.ActorOf(
            resolver.Props<ClassifierActor>(conversationId),
            $"classifier-{conversationId}");

        informativeActor = Context.ActorOf(
            resolver.Props<InformativeActor>(conversationId),
            $"informative-{conversationId}");

        dispositiveActor = Context.ActorOf(
            resolver.Props<DispositiveActor>(conversationId),
            $"dispositive-{conversationId}");

        ReceiveAsync<Records.UserMessage>(HandleUserMessageAsync);
    }

    private async Task HandleUserMessageAsync(Records.UserMessage msg)
    {
        IActorRef? senderRef = Sender;

        // 🔥 1) SE ABBIAMO UN EXECUTOR ATTIVO → bypassa classifier e agenti intermedi
        if (activeAgent != null)
        {
            logger.LogInformation(
                "Supervisor: follow-up detected, redirecting to active executor → {0}",
                activeAgent.Path);

            Records.ExecuteResponse executorFollowup;
            try
            {
                executorFollowup = await activeAgent.Ask<Records.ExecuteResponse>(
                    new Records.ExecuteRequest(msg.ConversationId, msg.Text, null));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Active follow-up executor did not reply.");
                activeAgent = null;

                senderRef.Tell(new Records.ConversationResponse("Si è verificato un errore interno.", null, null));
                return;
            }

            if (executorFollowup.IsCompleted)
            {
                logger.LogInformation("Executor signaled completion → clearing active executor.");
                activeAgent = null;
            }

            senderRef.Tell(new Records.ConversationResponse(executorFollowup.Response, null, null));
            return;
        }

        // 🔥 2) PRIMO TURNO: Guardia → Classificazione → Routing → Executor concreto
        try
        {
            Records.GuardCheckResponse? guardCheckResponse = await guardianActor.Ask<Records.GuardCheckResponse>(
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

            Records.ClassificationResult? classification = await classifierActor.Ask<Records.ClassificationResult>(msg);
            IActorRef routingAgent = classification.Category?.ToLower() switch
            {
                "informative" => informativeActor,
                "dispositive" => dispositiveActor,
                _ => informativeActor
            };

            logger.LogInformation("Supervisor: routing to agent {0}", routingAgent.Path);

            // Risposta polimorfica: può essere InternalExecuteResponse o ExecuteResponse
            object? executorResponse = await routingAgent.Ask<object>(
                new Records.ExecuteRequest(msg.ConversationId, msg.Text, classification));

            // Caso 1: InternalExecuteResponse → proveniente dall'agente di routing (InformativeActor/DispositiveActor)
            if (executorResponse is Records.InternalExecuteResponse internalExecuteResponse)
            {
                logger.LogInformation("Supervisor received InternalExecuteResponse from {0}", internalExecuteResponse.ExecutorRef.Path);

                // Se multi-turno → imposta executor concreto
                if (!internalExecuteResponse.IsCompleted)
                {
                    activeAgent = internalExecuteResponse.ExecutorRef;
                    logger.LogInformation("Supervisor: active executor set to {0}", activeAgent.Path);
                }

                senderRef.Tell(new Records.ConversationResponse(
                    internalExecuteResponse.Response,
                    classification.Category,
                    classification.Metadata));

                return;
            }

            // Caso 2: ExecuteResponse diretto (fallback, dovrebbe essere raro)
            if (executorResponse is Records.ExecuteResponse directExecuteResponse)
            {
                logger.LogWarning("Supervisor received direct ExecuteResponse instead of internal one.");

                if (!directExecuteResponse.IsCompleted)
                {
                    // fallback: usa l'agente intermedio come executor attivo (non ideale, ma evita blocchi)
                    activeAgent = routingAgent;
                    logger.LogWarning("Supervisor: fallback active executor = {0}", routingAgent.Path);
                }

                senderRef.Tell(new Records.ConversationResponse(
                    directExecuteResponse.Response,
                    classification.Category,
                    classification.Metadata));

                return;
            }

            // Caso 3: risposta inattesa
            logger.LogError("Supervisor received unexpected message type: {0}", executorResponse?.GetType());
            senderRef.Tell(new Records.ConversationResponse("Errore interno imprevisto.", null, null));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Errore nel gestire il primo turno.");
            senderRef.Tell(new Records.ConversationResponse("Si è verificato un errore interno.", null, null));
        }
    }
}