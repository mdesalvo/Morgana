using System.Text.Json;
using Akka.Actor;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Morgana.AI.Abstractions;
using Morgana.AI.Interfaces;
using static Morgana.AI.Records;
using static Morgana.Records;

namespace Morgana.Actors;

public class ClassifierActor : MorganaActor
{
    private readonly AIAgent classifierAgent;
    private readonly IPromptResolverService promptResolverService;
    private readonly ILogger<ClassifierActor> logger;

    public ClassifierActor(
        string conversationId,
        ILLMService llmService,
        IPromptResolverService promptResolverService,
        ILogger<ClassifierActor> logger) : base(conversationId)
    {
        this.logger = logger;
        this.promptResolverService = promptResolverService;

        Prompt classifierPrompt = promptResolverService.ResolveAsync("Classifier").GetAwaiter().GetResult();
        IntentCollection intentCollection = new IntentCollection(classifierPrompt.GetAdditionalProperty<List<Dictionary<string, string>>>("Intents"));
        string formattedIntents = string.Join("|", intentCollection.AsDictionary().Select(kvp => $"{kvp.Key} ({kvp.Value})"));
        string classifierPromptContent = $"{classifierPrompt.Content.Replace("((formattedIntents))", formattedIntents)}\n{classifierPrompt.Instructions}";
        classifierAgent = llmService.GetChatClient().CreateAIAgent(
            instructions: classifierPromptContent,
            name: "ClassifierActor");

        ReceiveAsync<UserMessage>(ClassifyMessageAsync);
    }

    private async Task ClassifyMessageAsync(UserMessage msg)
    {
        IActorRef senderRef = Sender;

        try
        {
            AgentRunResponse agentResponse = await classifierAgent.RunAsync(msg.Text);
            string jsonText = agentResponse.Text?.Trim()
                                                 .Replace("```json", "")
                                                 .Replace("```", "")
                                                 .Trim() ?? "{}";

            ClassificationResponse? classificationResponse = JsonSerializer.Deserialize<ClassificationResponse>(jsonText);
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