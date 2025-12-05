using Akka.Actor;
using Morgana.Messages;
using Morgana.Interfaces;
using System.Text.Json;

namespace Morgana.Agents;

public class GuardAgent : ReceiveActor
{
    private readonly ILLMService _llmService;
    private readonly string[] _prohibitedTerms = ["idiota", "stupido", "maledetto"];

    public GuardAgent(ILLMService llmService)
    {
        _llmService = llmService;
        ReceiveAsync<GuardCheckRequest>(CheckCompliance);
    }

    private async Task CheckCompliance(GuardCheckRequest req)
    {
        // Basic profanity check
        foreach (string term in _prohibitedTerms)
        {
            if (req.Message.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                Sender.Tell(new GuardCheckResponse(false, "Linguaggio inappropriato rilevato."));
                return;
            }
        }

        // Advanced LLM-based policy check
        string prompt = $@"Verifica se questo messaggio viola policy aziendali (spam, phishing, contenuti offensivi):

Messaggio: {req.Message}

Rispondi JSON: {{""compliant"": true/false, ""violation"": ""motivo o null""}}";

        string response = await _llmService.CompleteAsync(prompt);
        GuardCheckResponse? result = JsonSerializer.Deserialize<GuardCheckResponse>(response);

        Sender.Tell(new GuardCheckResponse(result.IsCompliant, result.Violation));
    }
}