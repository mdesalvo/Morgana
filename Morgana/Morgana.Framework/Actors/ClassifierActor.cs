using Akka.Actor;
using Akka.Event;
using Morgana.Framework.Abstractions;
using Morgana.Framework.Interfaces;
using System.Text.Json;

namespace Morgana.Framework.Actors;

/// <summary>
/// Intent classification actor that analyzes user messages to determine their underlying intent.
/// Uses LLM-based classification to match messages against configured intent definitions.
/// </summary>
/// <remarks>
/// <para><strong>Tell Pattern:</strong></para>
/// <para>This actor uses direct Sender.Tell() to reply to the supervisor, avoiding temporary actors
/// from Ask pattern. Replies with ClassificationResult or Status.Failure on errors.</para>
/// <para>Loads intent definitions from the domain configuration and formats them for LLM classification.
/// Falls back to "other" intent on classification failures to maintain conversation flow.</para>
/// </remarks>
public class ClassifierActor : MorganaActor
{
    /// <summary>
    /// Precomputed classifier prompt target with intent definitions embedded.
    /// Built once during actor initialization for performance.
    /// </summary>
    private readonly string classifierPromptTarget;

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
        classifierPromptTarget = $"{classifierPrompt.Target.Replace("((formattedIntents))", formattedIntents)}\n{classifierPrompt.Instructions}";

        // Analyze incoming user messages to determine their underlying intent:
        // - Invokes LLM with pre-formatted intent definitions
        // - Replies directly to Sender with ClassificationResult (Tell pattern, no Ask)
        // - Falls back to "other" intent on classification failures to maintain conversation flow
        ReceiveAsync<Records.UserMessage>(ClassifyMessageAsync);
    }

    /// <summary>
    /// Classifies a user message by invoking the LLM with the prepared classifier prompt.
    /// Returns a classification result with intent name and confidence score.
    /// </summary>
    /// <param name="msg">User message to classify</param>
    /// <remarks>
    /// <para><strong>Tell Pattern:</strong></para>
    /// <para>Captures sender reference early and replies directly via Sender.Tell().
    /// No internal Self.Tell() messages or wrapper records needed.</para>
    /// <para><strong>Error Handling:</strong></para>
    /// <para>On LLM failure, sends Status.Failure to supervisor for proper error handling.
    /// Supervisor will fall back to "other" intent to maintain conversation flow.</para>
    /// </remarks>
    private async Task ClassifyMessageAsync(Records.UserMessage msg)
    {
        IActorRef originalSender = Sender;

        actorLogger.Info($"Classifying message: '{msg.Text[..Math.Min(50, msg.Text.Length)]}...'");

        try
        {
            string response = await llmService.CompleteWithSystemPromptAsync(
                conversationId,
                classifierPromptTarget,
                msg.Text);

            Records.ClassificationResponse? classificationResponse = 
                JsonSerializer.Deserialize<Records.ClassificationResponse>(response);

            Records.ClassificationResult result = new Records.ClassificationResult(
                classificationResponse?.Intent ?? "other",
                new Dictionary<string, string>
                {
                    ["intent"] = classificationResponse?.Intent ?? "other",
                    ["confidence"] = (classificationResponse?.Confidence ?? 0.5).ToString("F2")
                });

            actorLogger.Info($"Classification complete: intent='{result.Intent}', " +
                $"confidence={result.Metadata.GetValueOrDefault("confidence", "N/A")}");

            originalSender.Tell(result);
        }
        catch (Exception ex)
        {
            actorLogger.Error(ex, "Error classifying message");

            // Send failure to supervisor for fallback handling
            originalSender.Tell(new Status.Failure(ex));
        }
    }
}