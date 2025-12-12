using Akka.Actor;
using Morgana.Interfaces;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using static Morgana.Records;

namespace Morgana.Actors;

public class ClassifierActor : MorganaActor
{
    private readonly AIAgent classifierAgent;
    private readonly ILogger<ClassifierActor> logger;

    public ClassifierActor(string conversationId, ILLMService llmService, ILogger<ClassifierActor> logger) : base(conversationId)
    {
        this.logger = logger;

        // Crea un agente dedicato alla classificazione
        classifierAgent = llmService.GetChatClient().CreateAIAgent(
            instructions:
"""
Sei un classificatore esperto di richieste clienti.
Classifica ogni richiesta in 'informative' (richiesta informazioni) o 'dispositive' (richiesta azione).
Identifica anche l'intento specifico tra: billing_retrieval, hardware_troubleshooting, contract_cancellation, other.
Rispondi SOLO con JSON in questo formato esatto (nessun markdown, nessun preamble):
{
    "category": "informative|dispositive",
    "intent": "billing_retrieval|hardware_troubleshooting|contract_cancellation|other",
    "confidence": numero tra 0 e 1 che esprime il livello di confidenza della valutazione
}
""",
            name: "ClassifierActor");

        ReceiveAsync<UserMessage>(ClassifyMessageAsync);
    }

    private async Task ClassifyMessageAsync(UserMessage msg)
    {
        IActorRef senderRef = Sender;

        try
        {
            string prompt = $"Classifica questa richiesta del cliente: {msg.Text}";

            AgentRunResponse response = await classifierAgent.RunAsync(prompt);
            string jsonText = response.Text?.Trim() ?? "{}";
            jsonText = jsonText.Replace("```json", "").Replace("```", "").Trim();

            ClassificationResponse? result = JsonSerializer.Deserialize<ClassificationResponse>(jsonText);
            ClassificationResult classification = new ClassificationResult(
                result?.Category ?? "informative",
                result?.Intent ?? "other",
                new Dictionary<string, string>
                {
                    ["confidence"] = (result?.Confidence ?? 0.5).ToString("F2"),
                    ["intent"] = result?.Intent ?? "other"
                });

            senderRef.Tell(classification);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error classifying message");

            // Fallback classification
            ClassificationResult fallback = new ClassificationResult(
                "informative",
                "other",
                new Dictionary<string, string> { ["confidence"] = "0.00", ["error"] = "classification_failed" });

            senderRef.Tell(fallback);
        }
    }
}