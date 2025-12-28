using System.Text.Json;
using Akka.Actor;
using Akka.Event;
using Morgana.AI.Abstractions;
using Morgana.AI.Interfaces;
using static Morgana.AI.Records;
using static Morgana.Records;

namespace Morgana.Actors;

public class ClassifierActor : MorganaActor
{
    private readonly string classifierPromptContent;

    public ClassifierActor(
        string conversationId,
        ILLMService llmService,
        IPromptResolverService promptResolverService,
        ILogger<ClassifierActor> _) : base(conversationId, llmService, promptResolverService)
    {
        Prompt classifierPrompt = promptResolverService.ResolveAsync("Classifier").GetAwaiter().GetResult();
        IntentCollection intentCollection = new IntentCollection(
            classifierPrompt.GetAdditionalProperty<List<Dictionary<string, string>>>("Intents"));
        
        string formattedIntents = string.Join("|", 
            intentCollection.AsDictionary().Select(kvp => $"{kvp.Key} ({kvp.Value})"));
        
        classifierPromptContent = $"{classifierPrompt.Content.Replace("((formattedIntents))", formattedIntents)}\n{classifierPrompt.Instructions}";

        ReceiveAsync<UserMessage>(ClassifyMessageAsync);
        Receive<AI.Records.ClassificationContext>(HandleClassificationResult);
        Receive<Status.Failure>(HandleFailure);
    }

    private async Task ClassifyMessageAsync(UserMessage msg)
    {
        IActorRef originalSender = Sender;

        actorLogger.Info($"Classifying message: '{msg.Text.Substring(0, Math.Min(50, msg.Text.Length))}...'");

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

    private void HandleClassificationResult(AI.Records.ClassificationContext ctx)
    {
        actorLogger.Info($"Classification complete: intent='{ctx.Result.Intent}', confidence={ctx.Result.Metadata.GetValueOrDefault("confidence", "N/A")}");
        ctx.OriginalSender.Tell(ctx.Result);
    }

    private void HandleFailure(Status.Failure failure)
    {
        actorLogger.Error(failure.Cause, "Classification pipeline failed");

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