using System.Text.Json;
using Akka.Actor;
using Akka.Event;
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
        Receive<LLMCheckContext>(HandleLLMCheckResult);
        Receive<Status.Failure>(HandleFailure);
    }

    private async Task CheckComplianceAsync(GuardCheckRequest req)
    {
        IActorRef originalSender = Sender;
        AI.Records.Prompt guardPrompt = await promptResolverService.ResolveAsync("Guard");

        // Basic profanity check (synchronous, immediate response)
        foreach (string term in guardPrompt.GetAdditionalProperty<List<string>>("ProfanityTerms"))
        {
            if (req.Message.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                actorLogger.Info($"Profanity detected: '{term}'");
                originalSender.Tell(new GuardCheckResponse(false, guardPrompt.GetAdditionalProperty<string>("LanguageViolation")));
                return;
            }
        }

        actorLogger.Info("Basic profanity check passed, engaging LLM policy check");

        // Advanced LLM-based policy check (async con salvaguardia sender)
        try
        {
            string response = await llmService.CompleteWithSystemPromptAsync(
                conversationId,
                $"{guardPrompt.Content}\n{guardPrompt.Instructions}",
                req.Message);

            GuardCheckResponse? result = JsonSerializer.Deserialize<GuardCheckResponse>(response);

            LLMCheckContext ctx = new LLMCheckContext(result ?? new GuardCheckResponse(true, null), originalSender);

            Self.Tell(ctx);
        }
        catch (Exception ex)
        {
            actorLogger.Error(ex, "LLM policy check failed");
            
            // Fallback su errore
            LLMCheckContext ctx = new LLMCheckContext(new GuardCheckResponse(true, null), originalSender);

            Self.Tell(ctx);
        }
    }

    private void HandleLLMCheckResult(LLMCheckContext ctx)
    {
        actorLogger.Info($"LLM policy check result: compliant={ctx.Response.Compliant}");
        ctx.OriginalSender.Tell(ctx.Response);
    }

    private void HandleFailure(Status.Failure failure)
    {
        actorLogger.Error(failure.Cause, "Guard check failed");
        
        // Fallback: assume compliant on error to avoid blocking legitimate requests
        Sender.Tell(new GuardCheckResponse(true, null));
    }
}