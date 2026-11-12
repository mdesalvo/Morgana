using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Morgana.AI.Interfaces;

namespace Morgana.AI.Services;

/// <summary>
/// Classifies a user message by model, ranking the configured intents and flagging the ones that collide with the top.
/// The Classifier prompt is composed once at construction with the domain's intents, so no turn pays for it.
/// </summary>
public class LLMClassifierService : IClassifierService
{
    /// <summary>
    /// The model of every classification, through the stateless completion on the framework tier.
    /// Classification keeps no memory of the conversation.
    /// </summary>
    private readonly ILanguageModelService llmService;

    /// <summary>
    /// Logs the intents loaded at construction and each classification's outcome, a collision included.
    /// The fallback to Other hides a failure from the user, so this log is where it shows.
    /// </summary>
    private readonly ILogger logger;

    /// <summary>
    /// The confidence gap under which a candidate collides with the top intent, from <c>Morgana:ActorSystem:IntentCollisionThreshold</c>.
    /// Absent, it is 0.10: ten points on the classifier's 0 to 1 scale.
    /// </summary>
    private readonly double disambiguationThreshold;

    /// <summary>The system prompt that every classification reads, composed at construction.</summary>
    private readonly string classifierSystemPrompt;

    /// <summary>The classification of a failure: the Other intent at zero confidence, marked as an error so the router shows no menu.</summary>
    private static readonly Records.ClassificationResult FallbackResult =
        new Records.ClassificationResult(
            Constants.Intents.Other,
            new Dictionary<string, string>
            {
                [Constants.ClassificationMetadata.Confidence] = "0.00",
                [Constants.ClassificationMetadata.Error] = "classification_failed"
            });

    /// <summary>Loads the domain's intents and composes the Classifier prompt with them, once for the process's life.</summary>
    public LLMClassifierService(
        ILanguageModelService llmService,
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
        // will fall through to Morgana's presentation.
        logger.LogInformation(
            intents.Count == 0
                ? $"{nameof(LLMClassifierService)}: no intents loaded — Morgana seems to be running in 'agentless' configuration"
                : $"{nameof(LLMClassifierService)}: loaded {intents.Count} intents for classification");

        // The intents keyed by name with their descriptions, which the prompt lists.
        Records.IntentCollection intentCollection = new Records.IntentCollection(intents);

        // Blocking like the intents above, for the same reason: nothing later rebuilds this prompt.
        Records.Prompt classifierPrompt =
            promptResolverService.ResolveAsync(Constants.Prompts.Classifier).GetAwaiter().GetResult();

        // The whole vocabulary the model may answer with, each name carrying the description that
        // teaches it what lands there. A name absent from this line cannot come back from a turn.
        // It closes on the complement of the domain — everything the domain does not cover — which
        // is this actor's own word and not an agent anybody modelled: no plugin declares it, it routes
        // to no agent and its description is authored here, in the prompt that reads it.
        string formattedIntents = string.Join("|",
            intentCollection.AsDictionary().Select(intentDescription => $"{intentDescription.Key} ({intentDescription.Value})")
                .Append($"{Constants.Intents.Other} ({classifierPrompt.GetAdditionalProperty<string>(Constants.PromptProperties.ComplementIntentDescription)})"));

        // What the classifier reads on every turn of this process's life: the three authored sections
        // with the domain's vocabulary spliced into the first.
        classifierSystemPrompt =
            string.Join("\n",
                Records.Prompt.Labeled(Constants.SectionLabels.Target, classifierPrompt.Target.Replace(Constants.Placeholders.FormattedIntents, formattedIntents)),
                Records.Prompt.Labeled(Constants.SectionLabels.Instructions, classifierPrompt.Instructions),
                Records.Prompt.Labeled(Constants.SectionLabels.Formatting, classifierPrompt.Formatting));
    }

    /// <inheritdoc/>
    public async Task<Records.ClassificationResult> ClassifyAsync(string conversationId, string message, CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            "LLMClassifierService: classifying message '{Preview}...' for conversation {ConversationId}",
            message[..Math.Min(50, message.Length)], conversationId);

        try
        {
            // The only model call of a classification, always on the framework tier: choosing
            // which agent a sentence belongs to is a routing decision, not domain reasoning.
            string response = await llmService.CompleteWithSystemPromptAsync(
                conversationId,
                classifierSystemPrompt,
                message,
                cancellationToken: cancellationToken);

            // The model answers in the shape the Classifier prompt's Formatting section asked for, so
            // anything unparseable here is that prompt drifting from this record.
            Records.ClassificationResponse? classificationResponse =
                JsonSerializer.Deserialize<Records.ClassificationResponse>(response, Records.DefaultJsonSerializerOptions);

            // The candidates by confidence, highest first: the top is the pick and the collisions are measured from it.
            List<Records.IntentScore> rankedIntentScores =
            [
                .. (classificationResponse?.Intents ?? [])
                .OrderByDescending(candidate => candidate.Confidence)
            ];

            // An empty list (null response, null/empty Intents array) means the model gave nothing
            // usable: that is a technical failure and not a genuine complement, so it takes the marked
            // fail-safe route and the router does not push a menu at a user whose request may have been legitimate.
            if (rankedIntentScores.Count == 0)
            {
                return FallbackResult with
                {
                    Metadata = new Dictionary<string, string>
                    {
                        [Constants.ClassificationMetadata.Confidence] = "0.00",
                        [Constants.ClassificationMetadata.Error] = "classification_empty"
                    }
                };
            }

            // The top of the ranked list is the classification's intent, whether or not a collision is flagged below.
            (string topIntentName, double topIntentScore) = rankedIntentScores[0];

            // Read back from telemetry and metadata by whoever consumes them, so it is written the
            // same on every host whatever its locale, as the fallbacks' literal "0.00" already is.
            string topIntentConfidence = topIntentScore.ToString("F2", CultureInfo.InvariantCulture);

            // The intent travels in the metadata too, which the classifier span records whole.
            Dictionary<string, string> metadata = new()
            {
                ["intent"] = topIntentName,
                [Constants.ClassificationMetadata.Confidence] = topIntentConfidence
            };

            // Any candidate within the threshold of the top score collides with it, the top itself included.
            // The Other intent never collides, since it has no button to offer.
            List<string> collidingIntents =
            [
                .. rankedIntentScores
                    .Where(candidate => !string.Equals(candidate.Intent, Constants.Intents.Other, StringComparison.OrdinalIgnoreCase))
                    .Where(candidate => topIntentScore - candidate.Confidence < disambiguationThreshold)
                    .Select(candidate => candidate.Intent)
            ];

            // The presence of the key diverts the turn to the disambiguation, so it is written only for two names or more:
            // the top scorer alone is no collision.
            if (collidingIntents.Count >= 2)
            {
                // Most confident first, the order in which the supervisor offers the buttons.
                metadata[Constants.ClassificationMetadata.AmbiguousIntents] = string.Join(",", collidingIntents);
                logger.LogInformation(
                    "LLMClassifierService: classification ambiguous — top='{Intent}', confidence={Confidence}, colliding=[{Colliding}]",
                    topIntentName, topIntentConfidence, metadata[Constants.ClassificationMetadata.AmbiguousIntents]);
            }
            // A clean pick routes to its agent.
            else
            {
                logger.LogInformation(
                    "LLMClassifierService: classification complete — intent='{Intent}', confidence={Confidence}",
                    topIntentName, topIntentConfidence);
            }

            // The supervisor routes on the intent or offers the collision.
            return new Records.ClassificationResult(topIntentName, metadata);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A classification stopped by its caller must not fall back to an answer.
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "LLMClassifierService: classification failed for conversation {ConversationId} — falling back to 'other'",
                conversationId);

            // The failure is marked with its cause, so the router answers with the classification-error message.
            return FallbackResult with
            {
                Metadata = new Dictionary<string, string>
                {
                    [Constants.ClassificationMetadata.Confidence] = "0.00",
                    [Constants.ClassificationMetadata.Error] = $"classification_failed: {ex.Message}"
                }
            };
        }
    }
}