using Akka.Actor;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Morgana.AI.Attributes;
using Morgana.AI.Interfaces;
using System.Reflection;
using System.Text;

namespace Morgana.AI.Abstractions;

public class MorganaAgent : MorganaActor
{
    protected AIAgent aiAgent;
    protected readonly ILogger<MorganaAgent> logger;

    //Local conversational memory (to be dismissed when the framework will support memories)
    protected readonly List<(string role, string text)> MessageHistory = [];

    //Local conversational context
    protected readonly Dictionary<string, object> AgentContext = [];

    public MorganaAgent(
        string conversationId,
        ILLMService llmService,
        IPromptResolverService promptResolverService,
        ILogger<MorganaAgent> logger) : base(conversationId, llmService, promptResolverService)
    {
        this.logger = logger;

        // NUOVO: handler per ricevere context updates da altri agenti
        Receive<Records.ReceiveContextUpdate>(HandleContextUpdate);
    }

    // NUOVO: Callback invocato quando un tool setta una variabile shared
    protected void OnSharedContextUpdate(string key, object value)
    {
        string intent = GetType().GetCustomAttribute<HandlesIntentAttribute>()?.Intent ?? "unknown";

        logger.LogInformation($"Agent {intent} broadcasting shared context variable: {key}");

        // Usa ActorSelection per trovare il RouterActor dinamicamente
        Context.ActorSelection($"/user/router-{conversationId}")
            .Tell(new Records.BroadcastContextUpdate(
                intent,
                new Dictionary<string, object> { [key] = value }
            ));
    }

    // NUOVO: Handler per ricevere context updates da altri agenti via RouterActor
    private void HandleContextUpdate(Records.ReceiveContextUpdate msg)
    {
        string myIntent = GetType().GetCustomAttribute<HandlesIntentAttribute>()?.Intent ?? "unknown";

        logger.LogWarning($">>> Agent '{myIntent}' HandleContextUpdate START - Current context: [{string.Join(", ", AgentContext.Keys)}]");

        foreach (var kvp in msg.UpdatedValues)
        {
            // Merge intelligente: aggiungi solo se NON esiste già (first-write-wins)
            if (!AgentContext.ContainsKey(kvp.Key))
            {
                AgentContext[kvp.Key] = kvp.Value;
                logger.LogInformation(
                    $"Agent '{myIntent}' received shared context '{kvp.Key}' = '{kvp.Value}' from '{msg.SourceAgentIntent}'"
                );
            }
            else
            {
                logger.LogDebug(
                    $"Agent '{myIntent}' ignored context update for '{kvp.Key}' " +
                    $"(already set to '{AgentContext[kvp.Key]}', source was '{msg.SourceAgentIntent}')"
                );
            }
        }

        logger.LogWarning($">>> Agent '{myIntent}' HandleContextUpdate END - Updated context: [{string.Join(", ", AgentContext.Keys)}]");
    }

    protected async Task ExecuteAgentAsync(Records.AgentRequest req)
    {
        IActorRef? senderRef = Sender;
        Records.Prompt morganaPrompt = await promptResolverService.ResolveAsync("Morgana");

        try
        {
            // Aggiungi messaggio utente allo storico
            MessageHistory.Add((role: nameof(ChatRole.User), text: req.Content!));

            StringBuilder sb = new StringBuilder();
            foreach ((string role, string msg) in MessageHistory)
                sb.AppendLine($"{role}: {msg}");

            AgentRunResponse llmResponse = await aiAgent.RunAsync(sb.ToString());
            string llmResponseText = llmResponse.Text ?? "";

            // verifica placeholder #INT#
            bool requiresMoreInput = llmResponseText.Contains("#INT#", StringComparison.OrdinalIgnoreCase);

            // aggiungi risposta assistente allo storico
            MessageHistory.Add((role: nameof(ChatRole.Assistant), text: llmResponseText));

            // aggiungi messaggio di sistema per consistenza dello storico (solo se serve più input)
            if (requiresMoreInput)
            {
                // Usa solo le Instructions specifiche dell'agente, non tutto il prompt Morgana
                string myIntent = GetType().GetCustomAttribute<HandlesIntentAttribute>()?.Intent ?? "other";
                Records.Prompt myPrompt = await promptResolverService.ResolveAsync(myIntent);
                MessageHistory.Add((role: nameof(ChatRole.System), text: myPrompt.Instructions));
            }

            // completed = false se serve input aggiuntivo (rimuovi placeholder #INT#)
            string cleanText = llmResponseText.Replace("#INT#", "", StringComparison.OrdinalIgnoreCase).Trim();
            senderRef.Tell(new Records.AgentResponse(cleanText, !requiresMoreInput));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Errore in {GetType().Name}");

            senderRef.Tell(new Records.AgentResponse(morganaPrompt.GetAdditionalProperty<string>("GenericErrorAnswer"), true));
        }
    }
}