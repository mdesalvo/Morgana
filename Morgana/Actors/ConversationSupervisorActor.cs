using Akka.Actor;
using Akka.DependencyInjection;
using Morgana.AI.Abstractions;
using Morgana.AI.Interfaces;

namespace Morgana.Actors;

public class ConversationSupervisorActor : MorganaActor
{
    private enum SupervisorState { Idle, ActiveAgent, PostCompletion }

    private readonly IActorRef guardActor;
    private readonly IActorRef classifierActor;
    private readonly IActorRef routerActor;
    private readonly ILogger<ConversationSupervisorActor> logger;

    // State management
    private SupervisorState state = SupervisorState.Idle;
    private IActorRef? activeAgent = null;
    private string? previousIntent = null;

    public ConversationSupervisorActor(
        string conversationId,
        ILLMService llmService,
        IPromptResolverService promptResolverService,
        ILogger<ConversationSupervisorActor> logger) : base(conversationId, llmService, promptResolverService)
    {
        this.logger = logger;

        DependencyResolver? resolver = DependencyResolver.For(Context.System);

        guardActor = Context.ActorOf(resolver.Props<GuardActor>(conversationId), $"guard-{conversationId}");
        classifierActor = Context.ActorOf(resolver.Props<ClassifierActor>(conversationId), $"classifier-{conversationId}");
        routerActor = Context.ActorOf(resolver.Props<RouterActor>(conversationId), $"router-{conversationId}");

        ReceiveAsync<Records.UserMessage>(HandleUserMessageAsync);
    }

    private async Task HandleUserMessageAsync(Records.UserMessage msg)
    {
        IActorRef senderRef = Sender;

        // ═══════════════════════════════════════════════════════════
        // CASO 1: Agente Attivo (Multi-Turn in corso)
        // ═══════════════════════════════════════════════════════════
        if (activeAgent != null)
        {
            logger.LogInformation("Follow-up detected, redirecting to active agent: {0}", activeAgent.Path);

            AI.Records.AgentResponse agentFollowup;
            try
            {
                agentFollowup = await activeAgent.Ask<AI.Records.AgentResponse>(
                    new AI.Records.AgentRequest(msg.ConversationId, msg.Text, null));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Active agent did not reply");
                activeAgent = null;
                state = SupervisorState.Idle;
                previousIntent = null;

                senderRef.Tell(new Records.ConversationResponse(
                    "Si è verificato un errore interno.", null, null));
                return;
            }

            // Se l'agente ha completato → Transizione a PostCompletion
            if (agentFollowup.IsCompleted)
            {
                logger.LogInformation("Agent completed - transitioning to PostCompletion state");
                state = SupervisorState.PostCompletion;
                // previousIntent già impostato quando l'agente è stato attivato
                activeAgent = null;
            }

            senderRef.Tell(new Records.ConversationResponse(agentFollowup.Response, null, null));
            return;
        }

        // ═══════════════════════════════════════════════════════════
        // CASO 2: Post-Completion Cooldown (1 Solo Turno!)
        // ═══════════════════════════════════════════════════════════
        if (state == SupervisorState.PostCompletion)
        {
            string lastIntent = previousIntent!;

            // RESETTA SEMPRE lo stato dopo questo turno (single-turn!)
            state = SupervisorState.Idle;
            previousIntent = null;

            // Valutazione LLM: è un acknowledgment/chiusura cortese?
            bool isAcknowledgment = await IsLikelyAcknowledgmentAsync(msg.Text, lastIntent);

            if (isAcknowledgment)
            {
                logger.LogInformation(
                    "Acknowledgment detected after intent '{0}' - routing to Morgana",
                    lastIntent);

                try
                {
                    string morganaResponse = await HandleMorganaAcknowledgmentAsync(msg.Text, lastIntent);

                    senderRef.Tell(new Records.ConversationResponse(
                        morganaResponse,
                        "acknowledgment",
                        new Dictionary<string, string>
                        {
                            ["previous_intent"] = lastIntent
                        }));
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Morgana acknowledgment failed");
                    senderRef.Tell(new Records.ConversationResponse(
                        "Sono qui se hai bisogno di altro!", null, null));
                }

                return;
            }

            // NON è un acknowledgment → Messaggio sostanziale
            logger.LogInformation(
                "Substantial message during cooldown - proceeding with normal classification");

            // Fall-through al CASO 3 (classificazione normale)
        }

        // ═══════════════════════════════════════════════════════════
        // CASO 3: Idle - Nuovo Intent (Guard → Classifier → Router)
        // ═══════════════════════════════════════════════════════════
        try
        {
            // Guard check
            Records.GuardCheckResponse guardCheck = await guardActor.Ask<Records.GuardCheckResponse>(
                new Records.GuardCheckRequest(msg.ConversationId, msg.Text));

            if (!guardCheck.Compliant)
            {
                AI.Records.Prompt guardPrompt = await promptResolverService.ResolveAsync("Guard");
                senderRef.Tell(new Records.ConversationResponse(
                    guardPrompt.GetAdditionalProperty<string>("GuardAnswer")
                        .Replace("((violation))", guardCheck.Violation!),
                    "guard_violation",
                    null));
                return;
            }

            // Classification
            AI.Records.ClassificationResult classification =
                await classifierActor.Ask<AI.Records.ClassificationResult>(msg);

            logger.LogInformation("Classification result: {0}", classification.Intent);

            // Router → Agent
            object agentResponse = await routerActor.Ask<object>(
                new AI.Records.AgentRequest(msg.ConversationId, msg.Text, classification));

            switch (agentResponse)
            {
                case AI.Records.InternalAgentResponse internalResponse:
                    logger.LogInformation(
                        "Received InternalAgentResponse from {0}",
                        internalResponse.AgentRef.Path);

                    // Se multi-turno → Imposta agente attivo
                    if (!internalResponse.IsCompleted)
                    {
                        activeAgent = internalResponse.AgentRef;
                        previousIntent = classification.Intent;
                        state = SupervisorState.ActiveAgent;

                        logger.LogInformation(
                            "Active agent set to {0} for intent '{1}'",
                            activeAgent.Path, previousIntent);
                    }
                    else
                    {
                        // Single-turn completato → PostCompletion
                        state = SupervisorState.PostCompletion;
                        previousIntent = classification.Intent;

                        logger.LogInformation(
                            "Single-turn intent '{0}' completed - entering PostCompletion",
                            previousIntent);
                    }

                    senderRef.Tell(new Records.ConversationResponse(
                        internalResponse.Response,
                        classification.Intent,
                        classification.Metadata));
                    break;

                case AI.Records.AgentResponse routerResponse:
                    logger.LogWarning(
                        "Received AgentResponse instead of InternalAgentResponse");

                    // Fallback: tratta come single-turn
                    state = SupervisorState.PostCompletion;
                    previousIntent = classification.Intent;

                    senderRef.Tell(new Records.ConversationResponse(
                        routerResponse.Response,
                        classification.Intent,
                        classification.Metadata));
                    break;

                default:
                    logger.LogError(
                        "Unexpected response type: {0}",
                        agentResponse?.GetType());

                    senderRef.Tell(new Records.ConversationResponse(
                        "Si è verificato un errore interno.", null, null));
                    break;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling message");

            // Reset completo in caso di errore
            state = SupervisorState.Idle;
            activeAgent = null;
            previousIntent = null;

            senderRef.Tell(new Records.ConversationResponse(
                "Si è verificato un errore interno.", null, null));
        }
    }

    private async Task<bool> IsLikelyAcknowledgmentAsync(string userText, string completedIntent)
    {
        try
        {
            AI.Records.Prompt morganaPrompt = await promptResolverService.ResolveAsync("Morgana");

            string systemPrompt = $@"{morganaPrompt.Content}

CONTESTO: Il cliente ha appena completato un'interazione relativa a '{completedIntent}'.

COMPITO: Valuta se il seguente messaggio è un semplice acknowledgment/chiusura cortese (es: 'grazie', 'ok', 'va bene', 'perfetto') oppure una nuova richiesta sostanziale.

RISPOSTA RICHIESTA: Rispondi SOLO con 'true' se è un acknowledgment, 'false' se è una richiesta sostanziale.
Nessun altro testo, nessuna spiegazione, solo 'true' o 'false'.";

            string response = await llmService.CompleteWithSystemPromptAsync(
                conversationId,
                systemPrompt,
                userText);

            string normalized = response.Trim().ToLowerInvariant();
            return normalized.Contains("true");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error evaluating acknowledgment - defaulting to false");
            return false; // In caso di errore, assume sia una richiesta sostanziale
        }
    }

    private async Task<string> HandleMorganaAcknowledgmentAsync(string userText, string completedIntent)
    {
        AI.Records.Prompt morganaPrompt = await promptResolverService.ResolveAsync("Morgana");

        string systemPrompt = $@"{morganaPrompt.Content}

CONTESTO SPECIALE: Il cliente ha appena completato un'interazione relativa a '{completedIntent}'.
Il suo messaggio sembra essere una conferma o chiusura cortese.

ISTRUZIONI:
1. Se è un semplice ringraziamento o conferma → Rispondi con cortesia e disponibilità futura
2. Mantieni la risposta BREVE (1-2 frasi max)
3. Non chiedere attivamente se ha altre domande (l'ha già fatto l'agente precedente)
4. Tono cordiale ma non invadente";

        return await llmService.CompleteWithSystemPromptAsync(
            conversationId,
            systemPrompt,
            userText);
    }

    protected override void PreStart()
    {
        logger.LogInformation("ConversationSupervisorActor started for {0}", conversationId);
        base.PreStart();
    }

    protected override void PostStop()
    {
        logger.LogInformation("ConversationSupervisorActor stopped for {0}", conversationId);
        base.PostStop();
    }
}