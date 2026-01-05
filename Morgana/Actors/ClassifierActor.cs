using System.Text.Json;
using Akka.Actor;
using Akka.Event;
using Morgana.AI.Abstractions;
using Morgana.AI.Interfaces;
using static Morgana.AI.Records;
using static Morgana.Records;

namespace Morgana.Actors;

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
        List<IntentDefinition> intents = agentConfigService.GetIntentsAsync().GetAwaiter().GetResult();
        if (intents.Count == 0)
        {
            actorLogger.Info("No intents loaded from domain. Classifier will have no intents to classify.");
        }
        else
        {
            actorLogger.Info($"Loaded {intents.Count} intents from domain for classification");
        }
        IntentCollection intentCollection = new IntentCollection(intents);
        
        // Format intents as "intent_name (description)" for LLM prompt
        string formattedIntents = string.Join("|", 
            intentCollection.AsDictionary().Select(kvp => $"{kvp.Key} ({kvp.Value})"));
        Prompt classifierPrompt = promptResolverService.ResolveAsync("Classifier").GetAwaiter().GetResult();
        classifierPromptContent = $"{classifierPrompt.Content.Replace("((formattedIntents))", formattedIntents)}\n{classifierPrompt.Instructions}";

        ReceiveAsync<UserMessage>(ClassifyMessageAsync);
        Receive<AI.Records.ClassificationContext>(HandleClassificationResult);
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
    private async Task ClassifyMessageAsync(UserMessage msg)
    {
        IActorRef originalSender = Sender;

        actorLogger.Info($"Classifying message: '{msg.Text[..Math.Min(50, msg.Text.Length)]}...'");

        try
        {
            string response = await llmService.CompleteWithSystemPromptAsync(
                conversationId,
                classifierPromptContent,
                msg.Text);

            ClassificationResponse? classificationResponse = JsonSerializer.Deserialize<ClassificationResponse>(response);
            
            ClassificationResult result = new ClassificationResult(
                classificationResponse?.Intent ?? "other",
                new Dictionary<string, string>
                {
                    ["intent"] = classificationResponse?.Intent ?? "other",
                    ["confidence"] = (classificationResponse?.Confidence ?? 0.5).ToString("F2")
                });

            AI.Records.ClassificationContext ctx = new AI.Records.ClassificationContext(result, originalSender);
            Self.Tell(ctx);
        }
        catch (Exception ex)
        {
            actorLogger.Error(ex, "Error classifying message");

            // Fallback to "other" intent on classification failure
            ClassificationResult fallback = new ClassificationResult(
                "other",
                new Dictionary<string, string>
                {
                    ["confidence"] = "0.00",
                    ["error"] = "classification_failed"
                });

            AI.Records.ClassificationContext ctx = new AI.Records.ClassificationContext(fallback, originalSender);
            Self.Tell(ctx);
        }
    }

    /// <summary>
    /// Handles the classification result after LLM processing.
    /// Routes the result back to the original sender.
    /// </summary>
    /// <param name="ctx">Context containing classification result and original sender reference</param>
    private void HandleClassificationResult(AI.Records.ClassificationContext ctx)
    {
        actorLogger.Info($"Classification complete: intent='{ctx.Result.Intent}', confidence={ctx.Result.Metadata.GetValueOrDefault("confidence", "N/A")}");
        ctx.OriginalSender.Tell(ctx.Result);
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
        ClassificationResult fallback = new ClassificationResult(
            "other",
            new Dictionary<string, string>
            {
                ["confidence"] = "0.00",
                ["error"] = "classification_pipeline_failed"
            });

        Sender.Tell(fallback);
    }
}