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
    private readonly ILogger<ClassifierActor> logger;

    public ClassifierActor(
        string conversationId,
        ILLMService llmService,
        ILogger<ClassifierActor> logger) : base(conversationId)
    {
        this.logger = logger;

        // Crea un agente dedicato alla classificazione
        classifierAgent = llmService.GetChatClient().CreateAIAgent(
            instructions:
"""
Sei un classificatore esperto di richieste clienti.
Classifica ogni richiesta del cliente determinandone l'intento, ovvero la tematica sottesa.
Tieni conto che al momento sei in grado di assolvere compiti inerenti tematiche di questo (breve) elenco:
- billing (richieste di visualizzazione dell'elenco di fatture o di spiegazione di voci di dettaglio specifiche)
- troubleshooting (richieste di soluzione di problemi tecnici dovuti a guasti che ingenerano disservizio)
- contract (richieste di informazioni sul contratto del cliente)
- other (qualsiasi altra tematica non espressamente intercettata)
Rispondi SOLO con JSON in questo formato esatto (nessun markdown, nessun preamble):
{
    "intent": "billing|contract|troubleshooting|other",
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
            string prompt = $"Classifica questa richiesta del cliente secondo le direttive che ti ho dato: {msg.Text}";

            AgentRunResponse agentResponse = await classifierAgent.RunAsync(prompt);
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
                new Dictionary<string, string> { ["confidence"] = "0.00", ["error"] = "classification_failed" });

            senderRef.Tell(fallback);
        }
    }
}