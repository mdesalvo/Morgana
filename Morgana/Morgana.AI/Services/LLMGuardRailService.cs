using Microsoft.Extensions.Logging;
using Morgana.AI.Interfaces;

namespace Morgana.AI.Services;

/// <summary>
/// Default <see cref="IGuardRailService"/> implementation providing two-level moderation strategy:
/// <list type="number">
///   <item><term>Fast synchronous profanity check</term>
///     <description>Scans the message against a configurable list of terms loaded from
///     <c>morgana.json → Guard → ProfanityTerms</c>. Rejects immediately on first match,
///     avoiding an unnecessary LLM round-trip.</description></item>
///   <item><term>Async LLM policy check</term>
///     <description>Delegates to <see cref="ILLMService"/> with the Guard system prompt for
///     nuanced detection of spam, phishing, violence, and other policy violations.</description></item>
/// </list>
/// </summary>
/// <remarks>
/// <para><strong>Fail-Open Behaviour:</strong></para>
/// <para>If the LLM call throws, the service logs the error and returns a compliant result
/// so legitimate traffic is never blocked by transient infrastructure failures.</para>
/// </remarks>
public class LLMGuardRailService : IGuardRailService
{
    private readonly ILLMService llmService;
    private readonly IPromptResolverService promptResolverService;
    private readonly ILogger logger;

    /// <summary>
    /// Initialises a new instance of <see cref="LLMGuardRailService"/>.
    /// </summary>
    /// <param name="llmService">LLM service used for the async policy check.</param>
    /// <param name="promptResolverService">Prompt resolver used to load Guard configuration.</param>
    /// <param name="logger">Logger for diagnostic output.</param>
    public LLMGuardRailService(
        ILLMService llmService,
        IPromptResolverService promptResolverService,
        ILogger logger)
    {
        this.llmService = llmService;
        this.promptResolverService = promptResolverService;
        this.logger = logger;
    }

    /// <inheritdoc/>
    public async Task<Records.GuardRailResult> CheckAsync(string conversationId, string message)
    {
        Records.Prompt guardPrompt = await promptResolverService.ResolveAsync("Guard");

        // ── Level 1: fast synchronous profanity check ─────────────────────────────
        foreach (string term in guardPrompt.GetAdditionalProperty<List<string>>("ProfanityTerms"))
        {
            if (message.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogInformation(
                    "LLMGuardRailService: profanity term '{Term}' detected in conversation {ConversationId}",
                    term, conversationId);

                return new Records.GuardRailResult(
                    Compliant: false,
                    Violation: guardPrompt.GetAdditionalProperty<string>("LanguageViolation"));
            }
        }

        logger.LogInformation(
            "LLMGuardRailService: profanity check passed for conversation {ConversationId}, proceeding to LLM policy check",
            conversationId);

        // ── Level 2: async LLM-based policy check ─────────────────────────────────
        try
        {
            string response = await llmService.CompleteWithSystemPromptAsync(
                conversationId,
                $"{guardPrompt.Target}\n{guardPrompt.Instructions}",
                message);

            Records.GuardCheckResponse? llmResult =
                System.Text.Json.JsonSerializer.Deserialize<Records.GuardCheckResponse>(response);

            bool compliant = llmResult?.Compliant ?? true;

            logger.LogInformation(
                "LLMGuardRailService: LLM policy check result — compliant={Compliant} for conversation {ConversationId}",
                compliant, conversationId);

            return llmResult != null
                ? new Records.GuardRailResult(llmResult.Compliant, llmResult.Violation)
                : new Records.GuardRailResult(Compliant: true, Violation: null);
        }
        catch (Exception ex)
        {
            // Fail open: a transient LLM error must not block legitimate users.
            logger.LogError(ex,
                "LLMGuardRailService: LLM policy check failed for conversation {ConversationId} — failing open",
                conversationId);

            return new Records.GuardRailResult(Compliant: true, Violation: null);
        }
    }
}