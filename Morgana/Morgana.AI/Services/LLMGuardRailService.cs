using Microsoft.Extensions.Logging;
using Morgana.AI.Interfaces;

namespace Morgana.AI.Services;

/// <summary>
/// Default <see cref="IGuardRailService"/> implementation. Delegates to <see cref="ILLMService"/>
/// with the Guard system prompt for detection of spam, phishing, violence, profanity and other
/// policy violations.
/// </summary>
public class LLMGuardRailService : IGuardRailService
{
    private readonly ILLMService llmService;
    private readonly ILogger logger;

    /// <summary>
    /// Pre-computed Guard system prompt.
    /// </summary>
    private readonly string guardSystemPrompt;

    /// <summary>
    /// Initialises a new instance of <see cref="LLMGuardRailService"/>.
    /// Loads and builds the Guard system prompt eagerly.
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
        this.logger = logger;

        Records.Prompt guardPrompt =
            promptResolverService.ResolveAsync("Guard").GetAwaiter().GetResult();

        guardSystemPrompt = $"{guardPrompt.Target}\n{guardPrompt.Instructions}\n{guardPrompt.Formatting}";
    }

    /// <inheritdoc/>
    public async Task<Records.GuardRailResult> CheckAsync(string conversationId, string message)
    {
        try
        {
            string response = await llmService.CompleteWithSystemPromptAsync(
                conversationId,
                guardSystemPrompt,
                message);

            Records.GuardCheckResponse? llmResult =
                System.Text.Json.JsonSerializer.Deserialize<Records.GuardCheckResponse>(response, Records.DefaultJsonSerializerOptions);

            bool compliant = llmResult?.Compliant ?? true;

            logger.LogInformation(
                "LLMGuardRailService: LLM policy check result — compliant={Compliant} for conversation {ConversationId}",
                compliant, conversationId);

            return llmResult != null
                ? new Records.GuardRailResult(llmResult.Compliant, llmResult.Violation)
                : new Records.GuardRailResult(Compliant: true, Violation: null);
        }
        catch (Exception ex) when (ex is System.ClientModel.ClientResultException { Status: 400 } cre
                                     && cre.Message.Contains("content_filter", StringComparison.OrdinalIgnoreCase))
        {
            // The provider's own content filter (e.g. Azure Prompt Shields) blocked the prompt before
            // any judgment could run — a genuine violation signal, never fail-open.
            logger.LogWarning(ex,
                "LLMGuardRailService: provider-level content filter rejected the prompt for conversation {ConversationId} — treating as a compliance violation",
                conversationId);

            return new Records.GuardRailResult(
                Compliant: false,
                Violation: "That is a path closed to you, and no phrasing will reopen it.");
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