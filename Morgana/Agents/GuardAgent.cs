using Akka.Actor;
using Morgana.Messages;
using Morgana.Interfaces;
using System.Text.Json;

namespace Morgana.Agents;

public class GuardAgent : MorganaAgent
{
    private readonly ILLMService _llmService;
    private readonly string[] _prohibitedTerms = ["stupido", "idiota", "incapace", "inetto"];

    public GuardAgent(string conversationId, string userId, ILLMService llmService) : base(conversationId, userId)
    {
        _llmService = llmService;

        ReceiveAsync<GuardCheckRequest>(CheckComplianceAsync);
    }

    private async Task CheckComplianceAsync(GuardCheckRequest req)
    {
        IActorRef originalSender = Sender;

        // Basic profanity check
        foreach (string term in _prohibitedTerms)
        {
            if (req.Message.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                originalSender.Tell(new GuardCheckResponse(false, "Linguaggio inappropriato rilevato."));
                return;
            }
        }

        // Advanced LLM-based policy check
        string prompt = $@"Verifica se questo messaggio viola policy aziendali (spam, phishing, contenuti offensivi):

Messaggio: {req.Message}

Rispondi JSON: {{""compliant"": true/false, ""violation"": ""motivo o null""}}";

        string response = await _llmService.CompleteAsync(prompt);
        GuardCheckResponse? result = JsonSerializer.Deserialize<GuardCheckResponse>(response);

        originalSender.Tell(new GuardCheckResponse(result.IsCompliant, result.Violation));
    }
}