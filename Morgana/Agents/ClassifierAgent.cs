using Akka.Actor;
using Morgana.Messages;
using Morgana.Interfaces;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Morgana.Agents;

public class ClassifierAgent : ReceiveActor
{
    private readonly AIAgent aiAgent;
    private readonly ILogger<ClassifierAgent> logger;

    public ClassifierAgent(ILLMService llmService, ILogger<ClassifierAgent> logger)
    {
        this.logger = logger;

        // Crea un agente dedicato alla classificazione
        aiAgent = llmService.GetChatClient().CreateAIAgent(
            instructions: @"Sei un classificatore esperto di richieste clienti.
                Classifica ogni richiesta in 'informative' (richiesta informazioni) o 'dispositive' (richiesta azione).
                Identifica anche l'intento specifico tra: billing_retrieval, hardware_troubleshooting, contract_cancellation, contract_info, service_info.
                Rispondi SOLO in formato JSON valido senza preamble.",
            name: "ClassifierAgent");

        ReceiveAsync<UserMessage>(ClassifyMessage);
    }

    private async Task ClassifyMessage(UserMessage msg)
    {
        try
        {
            string prompt = $@"Classifica questa richiesta cliente:

Richiesta: {msg.Content}

Rispondi SOLO con JSON in questo formato esatto (nessun markdown, nessun preamble):
{{
  ""category"": ""informative o dispositive"",
  ""intent"": ""billing_retrieval|hardware_troubleshooting|contract_cancellation|contract_info|service_info"",
  ""confidence"": 0.95
}}";

            AgentRunResponse response = await aiAgent.RunAsync(prompt);
            string jsonText = response.Text?.Trim() ?? "{}";

            // Rimuovi eventuali markdown fence
            jsonText = jsonText.Replace("```json", "").Replace("```", "").Trim();

            ClassificationResponse? result = JsonSerializer.Deserialize<ClassificationResponse>(jsonText);

            ClassificationResult classification = new ClassificationResult(
                result?.Category ?? "informative",
                result?.Intent ?? "service_info",
                new Dictionary<string, string>
                {
                    ["confidence"] = (result?.Confidence ?? 0.5).ToString("F2"),
                    ["intent"] = result?.Intent ?? "service_info"
                });

            Sender.Tell(classification);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error classifying message");

            // Fallback classification
            ClassificationResult fallback = new ClassificationResult(
                "informative",
                "service_info",
                new Dictionary<string, string> { ["confidence"] = "0.00", ["error"] = "classification_failed" });

            Sender.Tell(fallback);
        }
    }
}