using Akka.Actor;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Morgana.AI.Interfaces;
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
    }

    protected async Task ExecuteAgentAsync(Records.AgentRequest req)
    {
        IActorRef? senderRef = Sender;
        Records.Prompt morganaPrompt = await promptResolverService.ResolveAsync("Morgana");

        try
        {
            // aggiungi messaggio di sistema in testa allo storico
            if (MessageHistory.Count == 0)
                MessageHistory.Add((role: nameof(ChatRole.System), text: morganaPrompt.Content));

            // aggiungi messaggio utente allo storico
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

            // aggiungi messaggio di sistema per consistenza dello storico
            if (requiresMoreInput)
                MessageHistory.Add((role: nameof(ChatRole.System), text: morganaPrompt.Instructions));

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