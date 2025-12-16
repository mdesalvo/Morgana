using System.Text;
using Akka.Actor;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;
using Morgana.AI.Interfaces;

namespace Morgana.AI.Abstractions;

public class MorganaAgent : MorganaActor
{
    protected AIAgent aiAgent;
    protected ILLMService llmService;
    protected IPromptResolverService promptResolverService;
    protected readonly ILogger<MorganaAgent> logger;

    //Local conversational memory (to be dismissed when the framework will support memories)
    protected readonly List<(string role, string text)> history = [];

    public MorganaAgent(
        string conversationId,
        ILLMService llmService,
        IPromptResolverService promptResolverService,
        ILogger<MorganaAgent> logger) : base(conversationId)
    {
        this.llmService = llmService;
        this.promptResolverService = promptResolverService;
        this.logger = logger;
    }

    protected async Task ExecuteAgentAsync(Records.AgentRequest req)
    {
        IActorRef? senderRef = Sender;

        try
        {
            // aggiungi messaggio utente allo storico
            history.Add((role: "user", text: req.Content!));

            string prompt = await BuildPromptAsync(history);

            AgentRunResponse llmResponse = await aiAgent.RunAsync(prompt);
            string text = llmResponse.Text ?? "";

            // verifica placeholder #INT#
            bool requiresMoreInput = text.Contains("#INT#", StringComparison.OrdinalIgnoreCase);

            // pulizia testo prima di mandarlo al client
            string cleanText = text.Replace("#INT#", "", StringComparison.OrdinalIgnoreCase).Trim();

            // aggiungi risposta assistente allo storico
            history.Add((role: "assistant", text: cleanText));

            // completed = false se serve input aggiuntivo
            senderRef.Tell(new Records.AgentResponse(cleanText, !requiresMoreInput));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Errore in {GetType().Name}");

            senderRef.Tell(new Records.AgentResponse("Si è verificato un errore. La preghiamo di riprovare.", true));
        }
    }

    protected async Task<string> BuildPromptAsync(List<(string role, string text)> hist)
    {
        StringBuilder sb = new StringBuilder();

        Records.Prompt morganaPrompt = await promptResolverService.ResolveAsync("Morgana");
        
        sb.AppendLine(morganaPrompt.Content);
        sb.AppendLine(morganaPrompt.Instructions);

        foreach ((string role, string text) in hist)
            sb.AppendLine($"{role}: {text}");

        sb.AppendLine("assistant:");

        return sb.ToString();
    }
}