using Akka.Actor;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;
using Morgana.AI.Attributes;
using Morgana.AI.Interfaces;
using Morgana.AI.Providers;
using System.Reflection;

namespace Morgana.AI.Abstractions;

public class MorganaAgent : MorganaActor
{
    protected AIAgent aiAgent;
    protected AgentThread aiAgentThread;
    protected MorganaContextProvider contextProvider;
    protected readonly ILogger<MorganaAgent> logger;

    public MorganaAgent(
        string conversationId,
        ILLMService llmService,
        IPromptResolverService promptResolverService,
        ILogger<MorganaAgent> logger) : base(conversationId, llmService, promptResolverService)
    {
        this.logger = logger;

        Receive<Records.ReceiveContextUpdate>(HandleContextUpdate);
    }

    /// <summary>
    /// Callback invocato quando un tool setta una variabile shared
    /// Broadcast della variabile a tutti gli altri agenti via RouterActor
    /// </summary>
    protected void OnSharedContextUpdate(string key, object value)
    {
        string intent = GetType().GetCustomAttribute<HandlesIntentAttribute>()?.Intent ?? "unknown";

        logger.LogInformation($"Agent {intent} broadcasting shared context variable: {key}");

        Context.ActorSelection($"/user/router-{conversationId}")
            .Tell(new Records.BroadcastContextUpdate(
                intent,
                new Dictionary<string, object> { [key] = value }
            ));
    }

    /// <summary>
    /// Handler per ricevere context updates da altri agenti via RouterActor
    /// Merge intelligente: accetta solo variabili non già presenti (first-write-wins)
    /// </summary>
    private void HandleContextUpdate(Records.ReceiveContextUpdate msg)
    {
        string myIntent = GetType().GetCustomAttribute<HandlesIntentAttribute>()?.Intent ?? "unknown";

        logger.LogInformation(
            $"Agent '{myIntent}' received shared context from '{msg.SourceAgentIntent}': {string.Join(", ", msg.UpdatedValues.Keys)}");

        contextProvider.MergeSharedContext(msg.UpdatedValues);
    }

    /// <summary>
    /// Esegue l'agente utilizzando AgentThread per la gestione automatica
    /// della cronologia conversazionale e del contesto
    /// </summary>
    protected async Task ExecuteAgentAsync(Records.AgentRequest req)
    {
        IActorRef? senderRef = Sender;
        Records.Prompt morganaPrompt = await promptResolverService.ResolveAsync("Morgana");

        try
        {
            aiAgentThread ??= aiAgent.GetNewThread();

            AgentRunResponse llmResponse = await aiAgent.RunAsync(req.Content!, aiAgentThread);
            string llmResponseText = llmResponse.Text ?? "";

            bool requiresMoreInput = llmResponseText.Contains("#INT#", StringComparison.OrdinalIgnoreCase);
            string cleanText = llmResponseText.Replace("#INT#", "", StringComparison.OrdinalIgnoreCase).Trim();

            senderRef.Tell(new Records.AgentResponse(cleanText, !requiresMoreInput));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Errore in {GetType().Name}");

            List<Records.ErrorAnswer> errorAnswers = morganaPrompt.GetAdditionalProperty<List<Records.ErrorAnswer>>("ErrorAnswers");
            Records.ErrorAnswer? genericError = errorAnswers.FirstOrDefault(e => string.Equals(e.Name, "GenericError", StringComparison.OrdinalIgnoreCase));

            senderRef.Tell(new Records.AgentResponse(genericError?.Content ?? "Si è verificato un errore interno.", true));
        }
    }
}