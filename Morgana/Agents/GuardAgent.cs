using Akka.Actor;
using Morgana.Messages;
using Morgana.Interfaces;

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
        foreach (var term in _prohibitedTerms)
        {
            if (req.Message.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                Sender.Tell(new GuardCheckResponse(false, "Linguaggio inappropriato rilevato."));
                return;
            }
        }

        // Advanced LLM-based policy check
        var prompt = $@"Verifica se questo messaggio viola policy aziendali (spam, phishing, contenuti offensivi):

Messaggio: {req.Message}

Rispondi JSON: {{""compliant"": true/false, ""violation"": ""motivo o null""}}";

        var response = await _llmService.CompleteAsync(prompt);
        var result = System.Text.Json.JsonSerializer.Deserialize<GuardResponse>(response);

        Sender.Tell(new GuardCheckResponse(result.Compliant, result.Violation));
    }

    private record GuardResponse(bool Compliant, string? Violation);
}