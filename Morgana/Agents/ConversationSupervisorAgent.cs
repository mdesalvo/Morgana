using Akka.Actor;
using Akka.DependencyInjection;
using Morgana;
using Morgana.Agents;

public class ConversationSupervisorAgent : MorganaAgent
{
    private readonly IActorRef classifierAgent;
    private readonly IActorRef guardAgent;
    private readonly IActorRef informativeAgent;
    private readonly IActorRef dispositiveAgent;
    private readonly ILogger<ConversationSupervisorAgent> logger;

    // Executor attivo in multi-turno (BillingExecutor, HardwareExecutor, ecc.)
    private IActorRef? _activeExecutor = null;

    public ConversationSupervisorAgent(
        string conversationId,
        string userId,
        ILogger<ConversationSupervisorAgent> logger) : base(conversationId, userId)
    {
        this.logger = logger;

        DependencyResolver? resolver = DependencyResolver.For(Context.System);

        guardAgent = Context.ActorOf(
            resolver.Props<GuardAgent>(conversationId, userId),
            $"guard-{conversationId}");
        
        classifierAgent = Context.ActorOf(
            resolver.Props<ClassifierAgent>(conversationId, userId),
            $"classifier-{conversationId}");

        informativeAgent = Context.ActorOf(
            resolver.Props<InformativeAgent>(conversationId, userId),
            $"informative-{conversationId}");

        dispositiveAgent = Context.ActorOf(
            resolver.Props<DispositiveAgent>(conversationId, userId),
            $"dispositive-{conversationId}");

        ReceiveAsync<Records.UserMessage>(HandleUserMessageAsync);
    }

    private async Task HandleUserMessageAsync(Records.UserMessage msg)
    {
        IActorRef? originalSender = Sender;

        // 🔥 1) SE ABBIAMO UN EXECUTOR ATTIVO → bypassa classifier e agenti intermedi
        if (_activeExecutor != null)
        {
            logger.LogInformation(
                "Supervisor: follow-up detected, redirecting to active executor → {0}",
                _activeExecutor.Path);

            Records.ExecuteResponse executorFollowup;
            try
            {
                executorFollowup = await _activeExecutor.Ask<Records.ExecuteResponse>(
                    new Records.ExecuteRequest(msg.UserId, msg.ConversationId, msg.Text, null));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Active follow-up executor did not reply.");
                originalSender.Tell(new Records.ConversationResponse("Si è verificato un errore interno.", null, null));
                _activeExecutor = null;
                return;
            }

            if (executorFollowup.IsCompleted)
            {
                logger.LogInformation("Executor signaled completion → clearing active executor.");
                _activeExecutor = null;
            }

            originalSender.Tell(new Records.ConversationResponse(executorFollowup.Response, null, null));
            return;
        }

        // 🔥 2) PRIMO TURNO: Guardia → Classificazione → Routing → Executor concreto
        try
        {
            Records.GuardCheckResponse? guardCheckResponse = await guardAgent.Ask<Records.GuardCheckResponse>(new Records.GuardCheckRequest(msg.UserId, msg.Text));
            if (!guardCheckResponse.Compliant)
            {
                Records.ConversationResponse response = new Records.ConversationResponse(
                    $"La prego di mantenere un tono professionale. {guardCheckResponse.Violation}",
                    "guard_violation",
                    []);
                originalSender.Tell(response);
                return;
            }
            
            Records.ClassificationResult? classification = await classifierAgent.Ask<Records.ClassificationResult>(msg);
            IActorRef routingAgent = classification.Category?.ToLower() switch
            {
                "informative" => informativeAgent,
                "dispositive" => dispositiveAgent,
                _ => informativeAgent
            };

            logger.LogInformation("Supervisor: routing to agent {0}", routingAgent.Path);

            // Risposta polimorfica: può essere InternalExecuteResponse o ExecuteResponse
            object? executorResponse = await routingAgent.Ask<object>(
                new Records.ExecuteRequest(msg.UserId, msg.ConversationId, msg.Text, classification));

            // Caso 1: InternalExecuteResponse → proveniente dall'agente di routing (InformativeAgent/DispositiveAgent)
            if (executorResponse is Records.InternalExecuteResponse internalExecuteResponse)
            {
                logger.LogInformation("Supervisor received InternalExecuteResponse from {0}", internalExecuteResponse.ExecutorRef.Path);

                // Se multi-turno → imposta executor concreto
                if (!internalExecuteResponse.IsCompleted)
                {
                    _activeExecutor = internalExecuteResponse.ExecutorRef;
                    logger.LogInformation("Supervisor: active executor set to {0}", _activeExecutor.Path);
                }

                originalSender.Tell(new Records.ConversationResponse(
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
                    _activeExecutor = routingAgent;
                    logger.LogWarning("Supervisor: fallback active executor = {0}", routingAgent.Path);
                }

                originalSender.Tell(new Records.ConversationResponse(
                    directExecuteResponse.Response,
                    classification.Category,
                    classification.Metadata));

                return;
            }

            // Caso 3: risposta inattesa
            logger.LogError("Supervisor received unexpected message type: {0}", executorResponse?.GetType());
            originalSender.Tell(new Records.ConversationResponse("Errore interno imprevisto.", null, null));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Errore nel gestire il primo turno.");
            originalSender.Tell(new Records.ConversationResponse("Si è verificato un errore interno.", null, null));
        }
    }
}