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
        IActorRef? senderRef = Sender;

        //
        // 🔥 1) SE ABBIAMO UN EXECUTOR ATTIVO → bypassa classifier e agenti intermedi
        //
        if (_activeExecutor != null)
        {
            logger.LogInformation(
                "Supervisor: follow-up detected, redirecting to active executor → {0}",
                _activeExecutor.Path);

            Records.ExecuteResponse followup;
            try
            {
                followup = await _activeExecutor.Ask<Records.ExecuteResponse>(
                    new Records.ExecuteRequest(msg.UserId, msg.ConversationId, msg.Text, null),
                    TimeSpan.FromSeconds(30));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Active follow-up executor did not reply.");
                senderRef.Tell(new Records.ConversationResponse("Si è verificato un errore interno.", null, null));
                _activeExecutor = null;
                return;
            }

            if (followup.IsCompleted)
            {
                logger.LogInformation("Executor signaled completion → clearing active executor.");
                _activeExecutor = null;
            }

            senderRef.Tell(new Records.ConversationResponse(followup.Response, null, null));
            return;
        }

        //
        // 🔥 2) PRIMO TURNO: Classificazione → Routing → Executor concreto
        //
        try
        {
            Records.GuardCheckResponse? guardCheckResponse = await guardAgent.Ask<Records.GuardCheckResponse>(new Records.GuardCheckRequest(msg.UserId, msg.Text));
            if (!guardCheckResponse.Compliant)
            {
                Records.ConversationResponse response = new Records.ConversationResponse(
                    $"La prego di mantenere un tono professionale. {guardCheckResponse.Violation}",
                    "guard_violation",
                    []);
                senderRef.Tell(response);
                return;
            }
            
            Records.ClassificationResult? classification = await classifierAgent.Ask<Records.ClassificationResult>(msg);

            IActorRef intermediate = classification.Category?.ToLower() switch
            {
                "informative" => informativeAgent,
                "dispositive" => dispositiveAgent,
                _ => informativeAgent
            };

            logger.LogInformation("Supervisor: routing to agent {0}", intermediate.Path);

            // Risposta polimorfica: può essere InternalExecuteResponse o ExecuteResponse
            object? execRespObj = await intermediate.Ask<object>(
                new Records.ExecuteRequest(msg.UserId, msg.ConversationId, msg.Text, classification),
                TimeSpan.FromSeconds(30));

            //
            // Caso 1: InternalExecuteResponse → proveniente da InformativeAgent/DispositiveAgent
            //
            if (execRespObj is Records.InternalExecuteResponse internalResp)
            {
                logger.LogInformation("Supervisor received InternalExecuteResponse from {0}", internalResp.ExecutorRef.Path);

                // Se multi-turno → imposta executor concreto
                if (!internalResp.IsCompleted)
                {
                    _activeExecutor = internalResp.ExecutorRef;
                    logger.LogInformation("Supervisor: active executor set to {0}", _activeExecutor.Path);
                }

                senderRef.Tell(new Records.ConversationResponse(
                    internalResp.Response,
                    classification.Category,
                    classification.Metadata));

                return;
            }

            //
            // Caso 2: ExecuteResponse diretto (fallback, dovrebbe essere raro)
            //
            if (execRespObj is Records.ExecuteResponse simpleResp)
            {
                logger.LogWarning("Supervisor received simple ExecuteResponse instead of internal one.");

                if (!simpleResp.IsCompleted)
                {
                    // fallback: usa l'agente intermedio come executor attivo (non ideale, ma evita blocchi)
                    _activeExecutor = intermediate;
                    logger.LogWarning("Supervisor: fallback active executor = {0}", intermediate.Path);
                }

                senderRef.Tell(new Records.ConversationResponse(
                    simpleResp.Response,
                    classification.Category,
                    classification.Metadata));

                return;
            }

            //
            // Caso 3: risposta inattesa
            //
            logger.LogError("Supervisor received unexpected message type: {0}", execRespObj?.GetType());
            senderRef.Tell(new Records.ConversationResponse("Errore interno imprevisto.", null, null));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Errore nel gestire il primo turno.");
            senderRef.Tell(new Records.ConversationResponse("Si è verificato un errore interno.", null, null));
        }
    }
}
