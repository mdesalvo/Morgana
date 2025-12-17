using System.Text.Json;
using Akka.Actor;
using Morgana.AI.Abstractions;
using Morgana.AI.Interfaces;
using static Morgana.AI.Records;
using static Morgana.Records;

namespace Morgana.Actors;

public class ClassifierActor : MorganaActor
{
    private readonly string classifierPromptContent;
    private readonly ILogger<ClassifierActor> logger;

    public ClassifierActor(
        string conversationId,
        ILLMService llmService,
        IPromptResolverService promptResolverService,
        ILogger<ClassifierActor> logger) : base(conversationId, llmService, promptResolverService)
    {
        this.logger = logger;

        Prompt classifierPrompt = promptResolverService.ResolveAsync("Classifier").GetAwaiter().GetResult();
        IntentCollection intentCollection = new IntentCollection(classifierPrompt.GetAdditionalProperty<List<Dictionary<string, string>>>("Intents"));
        string formattedIntents = string.Join("|", intentCollection.AsDictionary().Select(kvp => $"{kvp.Key} ({kvp.Value})"));
        classifierPromptContent = $"{classifierPrompt.Content.Replace("((formattedIntents))", formattedIntents)}\n{classifierPrompt.Instructions}";

        ReceiveAsync<UserMessage>(ClassifyMessageAsync);
    }

    private async Task ClassifyMessageAsync(UserMessage msg)
    {
        IActorRef senderRef = Sender;

        try
        {
            string response = await llmService.CompleteWithSystemPromptAsync(classifierPromptContent, msg.Text);
            ClassificationResponse? classificationResponse = JsonSerializer.Deserialize<ClassificationResponse>(response);
            ClassificationResult classificationResult = new ClassificationResult(
                classificationResponse?.Intent ?? "other",
                new Dictionary<string, string>
                {
                    ["intent"] = classificationResponse?.Intent ?? "other",
                    ["confidence"] = (classificationResponse?.Confidence ?? 0.5).ToString("F2")
                });

            senderRef.Tell(classificationResult);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error classifying message");

            // Fallback classification
            ClassificationResult fallback = new ClassificationResult(
                "other",
                new Dictionary<string, string>
                {
                    ["confidence"] = "0.00",
                    ["error"] = "classification_failed"
                });

            senderRef.Tell(fallback);
        }
    }
}