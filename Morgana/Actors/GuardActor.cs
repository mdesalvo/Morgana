using System.Text.Json;
using Akka.Actor;
using Morgana.AI.Abstractions;
using Morgana.AI.Interfaces;
using static Morgana.Records;

namespace Morgana.Actors;

public class GuardActor : MorganaActor
{
    public GuardActor(
        string conversationId,
        ILLMService llmService,
        IPromptResolverService promptResolverService) : base(conversationId, llmService, promptResolverService)
    {
        ReceiveAsync<GuardCheckRequest>(CheckComplianceAsync);
    }

    private async Task CheckComplianceAsync(GuardCheckRequest req)
    {
        IActorRef senderRef = Sender;
        AI.Records.Prompt guardPrompt = await promptResolverService.ResolveAsync("Guard");

        // Basic profanity check
        foreach (string term in guardPrompt.GetAdditionalProperty<List<string>>("ProfanityTerms"))
        {
            if (req.Message.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                senderRef.Tell(new GuardCheckResponse(false, guardPrompt.GetAdditionalProperty<string>("LanguageViolation")));
                return;
            }
        }

        // Advanced LLM-based policy check
        string response = await llmService.CompleteWithSystemPromptAsync(
            conversationId,
            $"{guardPrompt.Content}\n{guardPrompt.Instructions}",
            req.Message);
        GuardCheckResponse? result = JsonSerializer.Deserialize<GuardCheckResponse>(response);

        senderRef.Tell(new GuardCheckResponse(result.Compliant, result.Violation));
    }
}