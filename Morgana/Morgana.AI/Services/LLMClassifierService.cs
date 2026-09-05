using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Morgana.AI.Interfaces;

namespace Morgana.AI.Services;

/// <summary>
/// Default <see cref="IClassifierService"/> implementation providing intent classification strategy:
/// <para>Loads intent definitions from <see cref="IAgentConfigurationService"/> and the Classifier
/// prompt from <see cref="IPromptResolverService"/> at construction time, then formats them into
/// the system prompt used for every LLM classification call.</para>
/// </summary>
public class LLMClassifierService : IClassifierService
{
    /// <summary>
    /// LLM used for every classification call. Consumed through the stateless completion path:
    /// classification keeps no per-conversation memory, so it always runs on the cheapest tier.
    /// </summary>
    private readonly ILLMService llmService;

    /// <summary>
    /// Logger for the intent load at construction and for each classification outcome, including
    /// whether a collision was flagged — the fail-safe path makes failures otherwise invisible.
    /// </summary>
    private readonly ILogger logger;

    /// <summary>
    /// Confidence gap below which two or more candidate intents are considered a collision
    /// rather than a clean top pick. From <c>Morgana:ActorSystem:IntentCollisionThreshold</c>,
    /// defaults to 0.10 (10 percentage points on the classifier's 0-1 confidence scale).
    /// </summary>
    private readonly double disambiguationThreshold;

    /// <summary>
    /// Pre-computed classifier system prompt
    /// </summary>
    private readonly string classifierSystemPrompt;

    /// <summary>
    /// Fallback result returned when classification fails.
    /// </summary>
    private static readonly Records.ClassificationResult FallbackResult =
        new Records.ClassificationResult(
            Constants.Intents.Other,
            new Dictionary<string, string>
            {
                ["confidence"] = "0.00",
                ["error"] = "classification_failed"
            });

    /// <summary>Loads intent definitions and builds the classifier system prompt eagerly.</summary>
    /// <param name="llmService">LLM service used for every classification call; always runs on the cheapest configured tier.</param>
    /// <param name="promptResolverService">Prompt resolver used to load the <c>Classifier</c> prompt, whose <c>((formattedIntents))</c> placeholder is interpolated once here.</param>
    /// <param name="agentConfigService">Source of the intent definitions from <c>agents.json</c>; an empty list is legal and means agentless mode.</param>
    /// <param name="configuration">Read for <c>Morgana:ActorSystem:IntentCollisionThreshold</c>.</param>
    /// <param name="logger">Logger for load-time and per-classification diagnostics.</param>
    public LLMClassifierService(
        ILLMService llmService,
        IPromptResolverService promptResolverService,
        IAgentConfigurationService agentConfigService,
        IConfiguration configuration,
        ILogger logger)
    {
        this.llmService = llmService;
        this.logger = logger;

        // The confidence gap under which two intents count as one ambiguity. Read once because it
        // governs every classification this process performs, never a single conversation.
        this.disambiguationThreshold = configuration.GetValue("Morgana:ActorSystem:IntentCollisionThreshold", 0.10);

        // Loads the intents the classifier prompt is built from, once, in a singleton's constructor:
        // there is no turn to run this on and nothing later rebuilds that prompt.
        List<Records.IntentDefinition> intents =
            agentConfigService.GetIntentsAsync().GetAwaiter().GetResult();

        // An empty domain is legal, so this line is the only warning an operator gets that every turn
        // will fall through to the unrecognized-intent answer.
        logger.LogInformation(
            intents.Count == 0
                ? $"{nameof(LLMClassifierService)}: no intents loaded — Morgana seems to be running in 'agentless' configuration"
                : $"{nameof(LLMClassifierService)}: loaded {intents.Count} intents for classification");

        Records.IntentCollection intentCollection = new Records.IntentCollection(intents);

        // The whole vocabulary the model may answer with, each name carrying the description that
        // teaches it what lands there. A name absent from this line cannot come back from a turn.
        string formattedIntents = string.Join("|",
            intentCollection.AsDictionary().Select(kvp => $"{kvp.Key} ({kvp.Value})"));

        // Blocking like the intents above, for the same reason: nothing later rebuilds this prompt.
        Records.Prompt classifierPrompt =
            promptResolverService.ResolveAsync(Constants.Prompts.Classifier).GetAwaiter().GetResult();

        // What the classifier reads on every turn of this process's life: the three authored sections
        // with the domain's vocabulary spliced into the first. Composed here so no turn pays for it.
        classifierSystemPrompt =
            $"{classifierPrompt.Target.Replace(Constants.Placeholders.FormattedIntents, formattedIntents)}\n{classifierPrompt.Instructions}\n{classifierPrompt.Formatting}";
    }

    /// <inheritdoc/>
    public async Task<Records.ClassificationResult> ClassifyAsync(string conversationId, string message)
    {
        logger.LogInformation(
            "LLMClassifierService: classifying message '{Preview}...' for conversation {ConversationId}",
            message[..Math.Min(50, message.Length)], conversationId);

        try
        {
            // The only model call of a classification, always on the cheapest configured tier: choosing
            // which desk a sentence belongs to is a routing decision, not domain reasoning.
            string response = await llmService.CompleteWithSystemPromptAsync(
                conversationId,
                classifierSystemPrompt,
                message);

            // The model answers in the shape the Classifier prompt's Formatting section asked for, so
            // anything unparseable here is that prompt drifting from this record.
            Records.ClassificationResponse? classificationResponse =
                JsonSerializer.Deserialize<Records.ClassificationResponse>(response, Records.DefaultJsonSerializerOptions);

            // Sort the candidates by confidence, highest first
            List<Records.IntentScore> rankedIntentScores =
            [
                .. (classificationResponse?.Intents ?? [])
                .OrderByDescending(candidate => candidate.Confidence)
            ];

            // An empty list (null response, null/empty Intents array) means the LLM gave us
            // nothing usable. The contract is fail-safe rather than throwing: the turn proceeds on
            // Intents.Other at middling confidence, which routes to no agent and is answered by the
            // router's unrecognized-intent fallback.
            if (rankedIntentScores.Count == 0)
                rankedIntentScores.Add(new Records.IntentScore(Constants.Intents.Other, 0.5));

            // The top of the ranked list is our "official" pick — the one that goes into
            // ClassificationResult.Intent and is used for normal (non-ambiguous) routing regardless
            // of whether we end up flagging a collision below.
            (string topIntentName, double topIntentScore) = rankedIntentScores[0];
            string topIntentConfidence = topIntentScore.ToString("F2");
            Dictionary<string, string> metadata = new()
            {
                ["intent"] = topIntentName,
                ["confidence"] = topIntentConfidence
            };

            // Any candidate within disambiguationThreshold of the TOP score collides with it.
            // Intents.Other never counts as a collision candidate.
            List<string> collidingIntents =
            [
                .. rankedIntentScores
                    .Where(candidate => !string.Equals(candidate.Intent, Constants.Intents.Other, StringComparison.OrdinalIgnoreCase))
                    .Where(candidate => topIntentScore - candidate.Confidence < disambiguationThreshold)
                    .Select(candidate => candidate.Intent)
            ];

            // The "ambiguousIntents" key's mere PRESENCE (not its value) is what ConversationSupervisorActor checks
            // to divert into disambiguation — a single name (just the top scorer) means no real collision.
            if (collidingIntents.Count >= 2)
            {
                // Comma-separated, in the same descending-confidence order as rankedIntentScores,
                // because that order is meaningful downstream: ConversationSupervisorActor builds
                // the disambiguation quick replies in this same order, so the most-likely option
                // is presented first to the user.
                metadata["ambiguousIntents"] = string.Join(",", collidingIntents);
                logger.LogInformation(
                    "LLMClassifierService: classification ambiguous — top='{Intent}', confidence={Confidence}, colliding=[{Colliding}]",
                    topIntentName, topIntentConfidence, metadata["ambiguousIntents"]);
            }
            else
            {
                logger.LogInformation(
                    "LLMClassifierService: classification complete — intent='{Intent}', confidence={Confidence}",
                    topIntentName, topIntentConfidence);
            }

            return new Records.ClassificationResult(topIntentName, metadata);
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