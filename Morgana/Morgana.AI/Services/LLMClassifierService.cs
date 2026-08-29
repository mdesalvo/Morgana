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
        this.disambiguationThreshold = configuration.GetValue("Morgana:ActorSystem:IntentCollisionThreshold", 0.10);

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
            promptResolverService.ResolveAsync(Constants.Prompts.Classifier).GetAwaiter().GetResult();

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
            string response = await llmService.CompleteWithSystemPromptAsync(
                conversationId,
                classifierSystemPrompt,
                message);

            // Step 1: parse the LLM's raw JSON into the wire DTO. This can legitimately come back
            // null (empty body) or with a null/missing "intents" array (malformed JSON that still
            // parses, e.g. `{}`) — both are handled below rather than treated as parse failures,
            // because System.Text.Json won't throw for a merely-incomplete-but-valid JSON object.
            Records.ClassificationResponse? classificationResponse =
                JsonSerializer.Deserialize<Records.ClassificationResponse>(response, Records.DefaultJsonSerializerOptions);

            // Step 2: sort the candidates by confidence, highest first. We do NOT trust the LLM to
            // have already sorted its own output correctly (prompts say "most-confident first", but
            // models drift), so this re-sort is cheap insurance rather than redundant work — the
            // whole collision check below silently assumes rankedIntentScores[0] is the true winner.
            List<Records.IntentScore> rankedIntentScores =
            [
                .. (classificationResponse?.Intents ?? [])
                .OrderByDescending(candidate => candidate.Confidence)
            ];

            // Step 3: an empty list (null response, null/empty Intents array) means the LLM gave us
            // nothing usable. This is the same "couldn't tell" case the old single-intent
            // implementation handled with its `?? "other"` fallback — we deliberately keep that same
            // fail-safe behaviour.
            if (rankedIntentScores.Count == 0)
                rankedIntentScores.Add(new Records.IntentScore(Constants.Intents.Other, 0.5));

            // Step 4: the top of the ranked list is our "official" pick — the one that goes into
            // ClassificationResult.Intent and is used for normal (non-ambiguous) routing regardless
            // of whether we end up flagging a collision below.
            (string topIntentName, double topIntentScore) = rankedIntentScores[0];
            string topIntentConfidence = topIntentScore.ToString("F2");

            // Step 5: any candidate within disambiguationThreshold of the TOP score collides with
            // it — every qualifying entry, not just the runner-up, so a three-way tie disambiguates
            // on all three. "other" never counts as a collision candidate: same exclusion as
            // IntentCollection.GetDisplayableIntents, since it has no Label/DefaultValue to render
            // as a quick reply — a real intent scoring close to "other" still routes normally.
            List<string> collidingIntents =
            [
                .. rankedIntentScores
                    .Where(candidate => !string.Equals(candidate.Intent, Constants.Intents.Other, StringComparison.OrdinalIgnoreCase))
                    .Where(candidate => topIntentScore - candidate.Confidence < disambiguationThreshold)
                    .Select(candidate => candidate.Intent)
            ];

            // Step 6: build the metadata bag every ClassificationResult carries. "intent" and
            // "confidence" are the two keys this dictionary has always had, since before
            // disambiguation existed — kept unchanged so nothing downstream that only reads those
            // two breaks.
            Dictionary<string, string> metadata = new()
            {
                ["intent"] = topIntentName,
                ["confidence"] = topIntentConfidence
            };

            // Step 7: the "ambiguousIntents" key's mere PRESENCE (not its value) is what
            // ConversationSupervisorActor checks to divert into disambiguation — a single name
            // (just the top scorer) means no real collision, so the key is left out entirely
            // rather than set to a one-element list. See ClassificationResult.Metadata's doc comment.
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