using System.Text.Json;
using Akka.Actor;
using Akka.Event;
using Microsoft.Extensions.Configuration;
using Morgana.Framework.Abstractions;
using Morgana.Framework.Interfaces;

namespace Morgana.Framework.Actors;

/// <summary>
/// Content moderation actor that performs two-level filtering on user messages.
/// First level: synchronous profanity check against a configured list.
/// Second level: asynchronous LLM-based policy check for complex content violations.
/// </summary>
/// <remarks>
/// <para><strong>Hybrid Approach for Performance:</strong></para>
/// <list type="bullet">
/// <item>Fast synchronous check for known bad terms (immediate rejection)</item>
/// <item>Slower async LLM check for nuanced policy violations (spam, phishing, violence, etc.)</item>
/// </list>
/// <para>Falls back to "compliant" status on errors to avoid blocking legitimate requests.</para>
/// </remarks>
public class GuardActor : MorganaActor
{
    /// <summary>
    /// Initializes a new instance of the GuardActor.
    /// </summary>
    /// <param name="conversationId">Unique identifier for this conversation</param>
    /// <param name="llmService">LLM service for policy-based content checks</param>
    /// <param name="promptResolverService">Service for resolving guard prompt templates</param>
    /// <param name="configuration">Morgana configuration (layered by ASP.NET)</param>
    public GuardActor(
        string conversationId,
        ILLMService llmService,
        IPromptResolverService promptResolverService,
        IConfiguration configuration) : base(conversationId, llmService, promptResolverService, configuration)
    {
        // Perform two-level content moderation check on incoming user messages:
        // 1. Fast synchronous profanity check against configured term list (immediate rejection if found)
        // 2. Slower async LLM-based policy check for complex violations (spam, phishing, violence, etc.)
        // Replies directly to Sender with GuardCheckResponse (Tell pattern, no Ask)
        ReceiveAsync<Records.GuardCheckRequest>(CheckComplianceAsync);
    }

    /// <summary>
    /// Performs two-level compliance check on user message.
    /// Level 1: Synchronous profanity check (immediate response if violation found).
    /// Level 2: Asynchronous LLM-based policy check (for spam, phishing, violence, etc.).
    /// </summary>
    /// <param name="req">Guard check request containing the message to validate</param>
    /// <remarks>
    /// <para><strong>Tell Pattern:</strong></para>
    /// <para>Captures sender reference early and replies directly via Sender.Tell().
    /// No internal Self.Tell() messages or wrapper records needed.</para>
    /// <para><strong>Error Handling:</strong></para>
    /// <para>On LLM failure, sends Status.Failure to supervisor for proper error handling.
    /// Supervisor will fall back to "compliant" status to avoid blocking legitimate requests.</para>
    /// </remarks>
    private async Task CheckComplianceAsync(Records.GuardCheckRequest req)
    {
        IActorRef originalSender = Sender;
        Records.Prompt guardPrompt = await promptResolverService.ResolveAsync("Guard");

        // Level 1: Basic profanity check (synchronous, fast)
        foreach (string term in guardPrompt.GetAdditionalProperty<List<string>>("ProfanityTerms"))
        {
            if (req.Message.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                actorLogger.Info($"Profanity detected: '{term}'");
                
                originalSender.Tell(new Records.GuardCheckResponse(
                    false, guardPrompt.GetAdditionalProperty<string>("LanguageViolation")));
                
                return;
            }
        }

        actorLogger.Info("Basic profanity check passed, engaging LLM policy check");

        // Level 2: Advanced LLM-based policy check (async, slower)
        try
        {
            string response = await llmService.CompleteWithSystemPromptAsync(
                conversationId,
                $"{guardPrompt.Target}\n{guardPrompt.Instructions}",
                req.Message);

            Records.GuardCheckResponse? result = JsonSerializer.Deserialize<Records.GuardCheckResponse>(response);

            actorLogger.Info($"LLM policy check result: compliant={result?.Compliant ?? true}");

            originalSender.Tell(result ?? new Records.GuardCheckResponse(true, null));
        }
        catch (Exception ex)
        {
            actorLogger.Error(ex, "LLM policy check failed");

            // Send failure to supervisor for fail-open handling
            originalSender.Tell(new Status.Failure(ex));
        }
    }
}