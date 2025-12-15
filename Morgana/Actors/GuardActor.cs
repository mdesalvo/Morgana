using System.Text.Json;
using Akka.Actor;
using Morgana.AI.Abstractions;
using Morgana.AI.Interfaces;
using static Morgana.Records;

namespace Morgana.Actors;

public class GuardActor : MorganaActor
{
    private readonly ILLMService llmService;
    private readonly IPromptResolverService promptResolverService;
    private readonly string[] prohibitedTerms = ["stupido", "idiota", "incapace", "inetto", "scemo"];

    public GuardActor(
        string conversationId,
        ILLMService llmService,
        IPromptResolverService promptResolverService) : base(conversationId)
    {
        this.llmService = llmService;
        this.promptResolverService = promptResolverService;

        ReceiveAsync<GuardCheckRequest>(CheckComplianceAsync);
    }

    private async Task CheckComplianceAsync(GuardCheckRequest req)
    {
        IActorRef senderRef = Sender;
        AI.Records.Prompt guardPrompt = await promptResolverService.ResolveAsync("Guard");

        // Basic profanity check
        foreach (string term in prohibitedTerms)
        {
            if (req.Message.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                senderRef.Tell(new GuardCheckResponse(false, guardPrompt.GetAdditionalProperty<string>("LanguageViolation")));
                return;
            }
        }

        // Advanced LLM-based policy check
        string response = await llmService.CompleteWithSystemPromptAsync($"{guardPrompt.Content}\n{guardPrompt.Instructions}", req.Message);
        GuardCheckResponse? result = JsonSerializer.Deserialize<GuardCheckResponse>(response);

        senderRef.Tell(new GuardCheckResponse(result.Compliant, result.Violation));
    }
}