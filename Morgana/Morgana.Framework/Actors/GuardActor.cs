using Akka.Actor;
using Akka.Event;
using Morgana.Framework.Abstractions;
using Morgana.Framework.Interfaces;
using System.Text.Json;

namespace Morgana.Framework.Actors;

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
        // Perform two-level content moderation check on incoming user messages:
        // 1. Fast synchronous profanity check against configured term list (immediate rejection if found)
        // 2. Slower async LLM-based policy check for complex violations (spam, phishing, violence, etc.)
        // Returns GuardCheckResponse with compliant flag and optional violation message
        ReceiveAsync<Records.GuardCheckRequest>(CheckComplianceAsync);
        Receive<Records.LLMCheckContext>(HandleLLMCheckResult);
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
    private async Task CheckComplianceAsync(Records.GuardCheckRequest req)
    {
        IActorRef originalSender = Sender;
        Records.Prompt guardPrompt = await promptResolverService.ResolveAsync("Guard");

        // Basic profanity check
        foreach (string term in guardPrompt.GetAdditionalProperty<List<string>>("ProfanityTerms"))
        {
            if (req.Message.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                actorLogger.Info($"Profanity detected: '{term}'");
                originalSender.Tell(new Records.GuardCheckResponse(false, guardPrompt.GetAdditionalProperty<string>("LanguageViolation")));
                return;
            }
        }

        actorLogger.Info("Basic profanity check passed, engaging LLM policy check");

        // Advanced LLM-based policy check
        try
        {
            string response = await llmService.CompleteWithSystemPromptAsync(
                conversationId,
                $"{guardPrompt.Target}\n{guardPrompt.Instructions}",
                req.Message);

            Records.GuardCheckResponse? result = JsonSerializer.Deserialize<Records.GuardCheckResponse>(response);

            Self.Tell(new Records.LLMCheckContext(result ?? new Records.GuardCheckResponse(true, null), originalSender));
        }
        catch (Exception ex)
        {
            actorLogger.Error(ex, "LLM policy check failed");

            // Fallback on error: assume compliant to avoid blocking legitimate requests
            Self.Tell(new Records.LLMCheckContext(new Records.GuardCheckResponse(true, null), originalSender));
        }
    }

    /// <summary>
    /// Handles the result of the LLM-based policy check.
    /// Routes the response back to the original sender.
    /// </summary>
    /// <param name="ctx">Context containing the LLM check result and original sender reference</param>
    private void HandleLLMCheckResult(Records.LLMCheckContext ctx)
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
        Sender.Tell(new Records.GuardCheckResponse(true, null));
    }
}