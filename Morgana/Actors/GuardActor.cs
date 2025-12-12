using System.Text.Json;
using Akka.Actor;
using Morgana.AI.Abstractions;
using Morgana.AI.Interfaces;
using static Morgana.Records;

namespace Morgana.Actors;

public class GuardActor : MorganaActor
{
    private readonly ILLMService _llmService;
    private readonly string[] _prohibitedTerms = ["stupido", "idiota", "incapace", "inetto"];

    public GuardActor(
        string conversationId,
        ILLMService llmService) : base(conversationId)
    {
        _llmService = llmService;

        ReceiveAsync<GuardCheckRequest>(CheckComplianceAsync);
    }

    private async Task CheckComplianceAsync(GuardCheckRequest req)
    {
        IActorRef senderRef = Sender;

        // Basic profanity check
        foreach (string term in _prohibitedTerms)
        {
            if (req.Message.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                senderRef.Tell(new GuardCheckResponse(false, "Linguaggio inappropriato rilevato."));
                return;
            }
        }

        // Advanced LLM-based policy check
        const string systemPrompt =
"""
Verifica se il messaggio del cliente viola policy aziendali (spam, phishing, violenza, parolacce, insulti, contenuti offensivi, tranelli, richieste fuorvianti)
Rispondi JSON: {"compliant": true/false, "violation": "motivo o null"}
""";

        string response = await _llmService.CompleteWithSystemPromptAsync(systemPrompt, req.Message);
        GuardCheckResponse? result = JsonSerializer.Deserialize<GuardCheckResponse>(response);

        senderRef.Tell(new GuardCheckResponse(result.Compliant, result.Violation));
    }
}