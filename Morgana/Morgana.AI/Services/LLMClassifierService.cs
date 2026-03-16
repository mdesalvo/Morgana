using System.Text.Json;
using Microsoft.Extensions.Logging;
using Morgana.AI.Interfaces;

namespace Morgana.AI.Services;

/// <summary>
/// Default <see cref="IClassifierService"/> implementation providing intent classification strategy:
/// <para>Loads intent definitions from <see cref="IAgentConfigurationService"/> and the Classifier
/// prompt from <see cref="IPromptResolverService"/> at construction time, then formats them into
/// the system prompt used for every LLM classification call.</para>
/// </summary>
/// <remarks>
/// <para><strong>Fail-Safe Behaviour:</strong></para>
/// <para>On any LLM or deserialization error, returns a fallback
/// <see cref="Records.ClassificationResult"/> with intent <c>"other"</c> and confidence
/// <c>0.0</c> so the conversation pipeline can continue without interruption.</para>
/// </remarks>
public class LLMClassifierService : IClassifierService
{
    private readonly ILLMService llmService;
    private readonly ILogger logger;

    /// <summary>
    /// Pre-computed classifier system prompt (intents + instructions).
    /// Built once at construction time for performance.
    /// </summary>
    private readonly string classifierPromptTarget;

    /// <summary>
    /// Fallback result returned when classification fails.
    /// </summary>
    private static readonly Records.ClassificationResult FallbackResult =
        new Records.ClassificationResult(
            "other",
            new Dictionary<string, string>
            {
                ["confidence"] = "0.00",
                ["error"] = "classification_failed"
            });

    /// <summary>
    /// Initialises a new instance of <see cref="LLMClassifierService"/>.
    /// Loads intent definitions and builds the classifier prompt eagerly.
    /// </summary>
    /// <param name="llmService">LLM service used for intent classification calls.</param>
    /// <param name="promptResolverService">Prompt resolver used to load the Classifier prompt.</param>
    /// <param name="agentConfigService">Agent configuration service used to load intent definitions.</param>
    /// <param name="logger">Logger for diagnostic output.</param>
    public LLMClassifierService(
        ILLMService llmService,
        IPromptResolverService promptResolverService,
        IAgentConfigurationService agentConfigService,
        ILogger logger)
    {
        this.llmService = llmService;
        this.logger = logger;

        // Build classifier prompt once — mirrors the logic previously in ClassifierActor constructor
        List<Records.IntentDefinition> intents =
            agentConfigService.GetIntentsAsync().GetAwaiter().GetResult();

        logger.LogInformation(
            intents.Count == 0
                ? $"{nameof(LLMClassifierService)}: no intents loaded — Morgana seems to be running in 'agentless' configuration"
                : $"{nameof(LLMClassifierService)}: loaded {intents.Count} intents for classification");

        Records.IntentCollection intentCollection = new Records.IntentCollection(intents);

        string formattedIntents = string.Join("|",
            intentCollection.AsDictionary().Select(kvp => $"{kvp.Key} ({kvp.Value})"));

        Records.Prompt classifierPrompt =
            promptResolverService.ResolveAsync("Classifier").GetAwaiter().GetResult();

        classifierPromptTarget =
            $"{classifierPrompt.Target.Replace("((formattedIntents))", formattedIntents)}\n{classifierPrompt.Instructions}";
    }

    /// <inheritdoc/>
    public async Task<Records.ClassificationResult> ClassifyAsync(string conversationId, string message)
    {
        logger.LogInformation(
            "LLMClassifierService: classifying message '{Preview}...' for conversation {ConversationId}",
            message[..Math.Min(50, message.Length)], conversationId);

        try
        {
            string response = await llmService.CompleteWithSystemPromptAsync(
                conversationId,
                classifierPromptTarget,
                message);

            Records.ClassificationResponse? classificationResponse =
                JsonSerializer.Deserialize<Records.ClassificationResponse>(response);

            string intent = classificationResponse?.Intent ?? "other";
            string confidence = (classificationResponse?.Confidence ?? 0.5).ToString("F2");

            logger.LogInformation(
                "LLMClassifierService: classification complete — intent='{Intent}', confidence={Confidence}",
                intent, confidence);

            return new Records.ClassificationResult(
                intent,
                new Dictionary<string, string>
                {
                    ["intent"] = intent,
                    ["confidence"] = confidence
                });
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "LLMClassifierService: classification failed for conversation {ConversationId} — falling back to 'other'",
                conversationId);

            return FallbackResult with
            {
                Metadata = new Dictionary<string, string>
                {
                    ["confidence"] = "0.00",
                    ["error"] = $"classification_failed: {ex.Message}"
                }
            };
        }
    }
}