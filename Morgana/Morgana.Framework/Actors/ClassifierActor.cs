using System.Text.Json;
using Akka.Actor;
using Akka.Event;
using Morgana.Framework.Abstractions;
using Morgana.Framework.Interfaces;

namespace Morgana.Framework.Actors;

/// <summary>
/// Intent classification actor that analyzes user messages to determine their underlying intent.
/// Uses LLM-based classification to match messages against configured intent definitions.
/// </summary>
/// <remarks>
/// This actor loads intent definitions from the domain configuration and formats them for LLM classification.
/// Returns classification results with intent name and confidence score.
/// Falls back to "other" intent on classification failures to maintain conversation flow.
/// </remarks>
public class ClassifierActor : MorganaActor
{
    /// <summary>
    /// Precomputed classifier prompt content with intent definitions embedded.
    /// Built once during actor initialization for performance.
    /// </summary>
    private readonly string classifierPromptContent;

    /// <summary>
    /// Initializes a new instance of the ClassifierActor.
    /// Loads intent definitions from configuration and prepares the classifier prompt.
    /// </summary>
    /// <param name="conversationId">Unique identifier for this conversation</param>
    /// <param name="llmService">LLM service for intent classification</param>
    /// <param name="promptResolverService">Service for resolving classifier prompt templates</param>
    /// <param name="agentConfigService">Service for loading intent definitions from domain</param>
    public ClassifierActor(
        string conversationId,
        ILLMService llmService,
        IPromptResolverService promptResolverService,
        IAgentConfigurationService agentConfigService) : base(conversationId, llmService, promptResolverService)
    {
        // Load intents from domain
        List<Records.IntentDefinition> intents = agentConfigService.GetIntentsAsync().GetAwaiter().GetResult();
        actorLogger.Info(intents.Count == 0
            ? "No intents loaded from domain. Classifier will have no intents to classify."
            : $"Loaded {intents.Count} intents from domain for classification");
        Records.IntentCollection intentCollection = new Records.IntentCollection(intents);

        // Format intents as "intent_name (description)" for LLM prompt
        string formattedIntents = string.Join("|",
            intentCollection.AsDictionary().Select(kvp => $"{kvp.Key} ({kvp.Value})"));
        Records.Prompt classifierPrompt = promptResolverService.ResolveAsync("Classifier").GetAwaiter().GetResult();
        classifierPromptContent = $"{classifierPrompt.Target.Replace("((formattedIntents))", formattedIntents)}\n{classifierPrompt.Instructions}";

        // Analyze incoming user messages to determine their underlying intent:
        // - Invokes LLM with pre-formatted intent definitions
        // - Returns ClassificationResult with intent name and confidence score
        // - Falls back to "other" intent on classification failures to maintain conversation flow
        ReceiveAsync<Records.UserMessage>(ClassifyMessageAsync);
        Receive<Records.ClassificationContext>(HandleClassificationResult);
        Receive<Status.Failure>(HandleFailure);
    }

    /// <summary>
    /// Classifies a user message by invoking the LLM with the prepared classifier prompt.
    /// Returns a classification result with intent name and confidence score.
    /// </summary>
    /// <param name="msg">User message to classify</param>
    /// <remarks>
    /// Captures the original sender before async operations to ensure correct response routing.
    /// Falls back to "other" intent with 0.5 confidence on classification errors.
    /// </remarks>
    private async Task ClassifyMessageAsync(Records.UserMessage msg)
    {
        IActorRef originalSender = Sender;

        actorLogger.Info($"Classifying message: '{msg.Text[..Math.Min(50, msg.Text.Length)]}...'");

        try
        {
            string response = await llmService.CompleteWithSystemPromptAsync(
                conversationId,
                classifierPromptContent,
                msg.Text);

            Records.ClassificationResponse? classificationResponse = JsonSerializer.Deserialize<Records.ClassificationResponse>(response);

            Records.ClassificationResult result = new Records.ClassificationResult(
                classificationResponse?.Intent ?? "other",
                new Dictionary<string, string>
                {
                    ["intent"] = classificationResponse?.Intent ?? "other",
                    ["confidence"] = (classificationResponse?.Confidence ?? 0.5).ToString("F2")
                });

            Self.Tell(new Records.ClassificationContext(result, null, originalSender));
        }
        catch (Exception ex)
        {
            actorLogger.Error(ex, "Error classifying message");

            // Fallback to "other" intent on classification failure
            Records.ClassificationResult fallback = new Records.ClassificationResult(
                "other",
                new Dictionary<string, string>
                {
                    ["confidence"] = "0.00",
                    ["error"] = "classification_failed"
                });

            Self.Tell(new Records.ClassificationContext(fallback, null, originalSender));
        }
    }

    /// <summary>
    /// Handles the classification result after LLM processing.
    /// Routes the result back to the original sender.
    /// </summary>
    /// <param name="ctx">Context containing classification result and original sender reference</param>
    private void HandleClassificationResult(Records.ClassificationContext ctx)
    {
        actorLogger.Info($"Classification complete: intent='{ctx.Classification.Intent}', " +
            $"confidence={ctx.Classification.Metadata.GetValueOrDefault("confidence", "N/A")}");

        ctx.OriginalSender.Tell(ctx.Classification);
    }

    /// <summary>
    /// Handles failures during classification processing.
    /// Falls back to "other" intent to maintain conversation flow.
    /// </summary>
    /// <param name="failure">Failure information</param>
    private void HandleFailure(Status.Failure failure)
    {
        actorLogger.Error(failure.Cause, "Classification pipeline failed");

        // Fallback to "other" intent on pipeline failure
        Records.ClassificationResult fallback = new Records.ClassificationResult(
            "other",
            new Dictionary<string, string>
            {
                ["confidence"] = "0.00",
                ["error"] = "classification_pipeline_failed"
            });

        Sender.Tell(fallback);
    }
}