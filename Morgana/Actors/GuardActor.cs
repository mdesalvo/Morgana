using System.Text.Json;
using Akka.Actor;
using Akka.Event;
using Morgana.AI.Abstractions;
using Morgana.AI.Interfaces;
using static Morgana.Records;

namespace Morgana.Actors;

/// <summary>
/// Content moderation actor that performs two-level filtering on user messages.
/// First level: synchronous profanity check against a configured list.
/// Second level: asynchronous LLM-based policy check for complex content violations.
/// </summary>
/// <remarks>
/// This actor uses a hybrid approach for performance:
/// - Fast synchronous check for known bad terms (immediate rejection)
/// - Slower async LLM check for nuanced policy violations (spam, phishing, violence, etc.)
/// Falls back to "compliant" status on errors to avoid blocking legitimate requests.
/// </remarks>
public class GuardActor : MorganaActor
{
    /// <summary>
    /// Initializes a new instance of the GuardActor.
    /// </summary>
    /// <param name="conversationId">Unique identifier for this conversation</param>
    /// <param name="llmService">LLM service for policy-based content checks</param>
    /// <param name="promptResolverService">Service for resolving guard prompt templates</param>
    public GuardActor(
        string conversationId,
        ILLMService llmService,
        IPromptResolverService promptResolverService) : base(conversationId, llmService, promptResolverService)
    {
        ReceiveAsync<GuardCheckRequest>(CheckComplianceAsync);
        Receive<LLMCheckContext>(HandleLLMCheckResult);
        Receive<Status.Failure>(HandleFailure);
    }

    /// <summary>
    /// Performs two-level compliance check on user message.
    /// Level 1: Synchronous profanity check (immediate response if violation found).
    /// Level 2: Asynchronous LLM-based policy check (for spam, phishing, violence, etc.).
    /// </summary>
    /// <param name="req">Guard check request containing the message to validate</param>
    /// <remarks>
    /// The sender reference is captured before async operations to ensure correct message routing.
    /// LLM check is only performed if basic profanity check passes.
    /// </remarks>
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

        // Advanced LLM-based policy check (async with sender safeguard)
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
            
            // Fallback on error: assume compliant to avoid blocking legitimate requests
            LLMCheckContext ctx = new LLMCheckContext(new GuardCheckResponse(true, null), originalSender);

            Self.Tell(ctx);
        }
    }

    /// <summary>
    /// Handles the result of the LLM-based policy check.
    /// Routes the response back to the original sender.
    /// </summary>
    /// <param name="ctx">Context containing the LLM check result and original sender reference</param>
    private void HandleLLMCheckResult(LLMCheckContext ctx)
    {
        actorLogger.Info($"LLM policy check result: compliant={ctx.Response.Compliant}");
        ctx.OriginalSender.Tell(ctx.Response);
    }

    /// <summary>
    /// Handles failures during guard check processing.
    /// Falls back to "compliant" status to avoid blocking legitimate requests on errors.
    /// </summary>
    /// <param name="failure">Failure information</param>
    private void HandleFailure(Status.Failure failure)
    {
        actorLogger.Error(failure.Cause, "Guard check failed");
        
        // Fallback: assume compliant on error to avoid blocking legitimate requests
        Sender.Tell(new GuardCheckResponse(true, null));
    }
}