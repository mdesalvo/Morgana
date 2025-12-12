using Akka.Actor;
using Morgana.Adapters;
using System.Text;
using Microsoft.Agents.AI;
using Morgana.Interfaces;
using Morgana.Actors;

namespace Morgana.AI.Agents;

public class BillingAgent : MorganaActor
{
    private readonly AIAgent aiAgent;
    private readonly ILogger<BillingAgent> logger;

    // memoria conversazionale locale
    private readonly List<(string role, string text)> history = new();

    public BillingAgent(
        string conversationId,
        ILLMService llmService,
        ILogger<BillingAgent> logger) : base(conversationId)
    {
        this.logger = logger;

        AgentAdapter adapter = new AgentAdapter(llmService.GetChatClient());
        aiAgent = adapter.CreateBillingAgent();

        ReceiveAsync<Records.ExecuteRequest>(ExecuteBillingAsync);
    }

    private async Task ExecuteBillingAsync(Records.ExecuteRequest req)
    {
        IActorRef? senderRef = Sender;

        try
        {
            // aggiungi messaggio utente allo storico
            history.Add(("user", req.Content));

            string prompt = BuildPrompt(history);

            AgentRunResponse llmResponse = await aiAgent.RunAsync(prompt);
            string text = llmResponse.Text ?? "";

            // verifica placeholder #INT#
            bool requiresMoreInput = text.Contains("#INT#", StringComparison.OrdinalIgnoreCase);

            // pulizia testo prima di mandarlo al client
            string cleanText = text.Replace("#INT#", "", StringComparison.OrdinalIgnoreCase).Trim();

            // aggiungi risposta assistente allo storico
            history.Add(("assistant", cleanText));

            // completed = false se serve input aggiuntivo
            senderRef.Tell(new Records.ExecuteResponse(cleanText, !requiresMoreInput));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Errore in BillingAgent");

            senderRef.Tell(new Records.ExecuteResponse(
                "Si è verificato un errore. La preghiamo di riprovare.",
                true
            ));
        }
    }

    private string BuildPrompt(List<(string role, string text)> hist)
    {
        StringBuilder sb = new StringBuilder();

        sb.AppendLine("Conversazione in corso tra utente e assistente billing.");
        sb.AppendLine("Mantieni coerenza con lo storico e politiche definite nelle instructions.");

        foreach ((string role, string text) in hist)
        {
            sb.AppendLine($"{role}: {text}");
        }

        sb.AppendLine("assistant:");

        return sb.ToString();
    }
}