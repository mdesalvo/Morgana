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
Classifica ogni richiesta del cliente determinandone l'intento, ovvero la tematica sottesa.
Tieni conto che al momento sei in grado di assolvere compiti inerenti tematiche di (elenco degli intenti "noti"):
- billing_retrieval (richieste di visualizzazione dell'elenco di fatture o di estrazione di dettagli da una di esse)
- hardware_troubleshooting (richieste di soluzione di problemi tecnici dovuti a guatsi o disservizi)
- contract_cancellation (richieste di informazioni sul processo di cancellazione del contratto del cliente)
- other (qualsiasi altra tematica non espressamente intercettata)
Rispondi SOLO con JSON in questo formato esatto (nessun markdown, nessun preamble):
{
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
                result?.Intent ?? "other",
                new Dictionary<string, string>
                {
                    ["intent"] = result?.Intent ?? "other",
                    ["confidence"] = (result?.Confidence ?? 0.5).ToString("F2")
                });

            senderRef.Tell(classification);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error classifying message");

            // Fallback classification
            ClassificationResult fallback = new ClassificationResult(
                "other",
                new Dictionary<string, string> { ["confidence"] = "0.00", ["error"] = "classification_failed" });

            senderRef.Tell(fallback);
        }
    }
}