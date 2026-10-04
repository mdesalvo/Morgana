using System.Text.Json;
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
    /// <summary>
    /// LLM used for the policy check. Consumed through the stateless completion path — each
    /// message is judged on its own text, on the cheapest configured tier.
    /// </summary>
    private readonly ILLMService llmService;

    /// <summary>
    /// Logger for compliance verdicts and for the fail-open path, which admits a message the
    /// check could not evaluate and would otherwise leave no trace.
    /// </summary>
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

        // Built once here, not per-call: unlike LLMClassifierService/EmbeddedAgentConfigurationService
        // this isn't deferred behind a Lazy<> — the Guard prompt is needed on essentially every
        // turn (guard check gates every user message), so eager beats lazy for the common case.
        Records.Prompt guardPrompt =
            promptResolverService.ResolveAsync(Constants.Prompts.Guard).GetAwaiter().GetResult();

        // What the Guard reads before every user message. No placeholder to splice, unlike the
        // classifier's: what is admissible is a policy of the framework, not of a domain.
        guardSystemPrompt = $"{guardPrompt.Target}\n{guardPrompt.Instructions}\n{guardPrompt.Formatting}";
    }

    /// <inheritdoc/>
    public async Task<Records.GuardRailResult> CheckAsync(string conversationId, string message)
    {
        try
        {
            // The first call of every turn, on the cheapest configured tier: it stands between the user
            // and the whole pipeline, so nothing downstream runs until it has answered.
            string response = await llmService.CompleteWithSystemPromptAsync(
                conversationId,
                guardSystemPrompt,
                message);

            // The verdict, in the shape the Guard prompt's Formatting section asked for. Nothing here
            // is null when the model answered as instructed.
            Records.GuardCheckResponse? llmResult = JsonSerializer.Deserialize<Records.GuardCheckResponse>(
                response, Records.DefaultJsonSerializerOptions);

            // For the log line alone. The answer below is decided on the verdict itself.
            bool compliant = llmResult?.Compliant ?? true;

            logger.LogInformation(
                "LLMGuardRailService: LLM policy check result — compliant={Compliant} for conversation {ConversationId}",
                compliant, conversationId);

            // A verdict that could not be read admits the message. Refusing a user over a model that
            // answered badly would silence a legitimate turn, so the doubt is spent in their favour —
            // the same choice the general catch below makes.
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
                Violation: "That is a path closed to you and no phrasing will reopen it.");
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