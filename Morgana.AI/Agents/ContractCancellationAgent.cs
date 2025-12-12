using Akka.Actor;
using Microsoft.Agents.AI;
using Morgana.AI.Adapters;
using Morgana.AI.Actors;
using Morgana.AI.Interfaces;
using Microsoft.Extensions.Logging;

namespace Morgana.AI.Agents;

public class ContractCancellationAgent : MorganaActor
{
    private readonly AIAgent aiAgent;
    private readonly ILogger<ContractCancellationAgent> logger;

    public ContractCancellationAgent(
        string conversationId,
        ILLMService llmService,
        ILogger<ContractCancellationAgent> logger) : base(conversationId)
    {
        this.logger = logger;

        AgentAdapter adapter = new AgentAdapter(llmService.GetChatClient());
        aiAgent = adapter.CreateContractAgent();

        ReceiveAsync<Records.AgentRequest>(ExecuteCancellationAsync);
    }

    private async Task ExecuteCancellationAsync(Records.AgentRequest req)
    {
        IActorRef senderRef = Sender;

        try
        {
            logger.LogInformation($"Executing contract operation for conversation {req.ConversationId}");

            AgentRunResponse response = await aiAgent.RunAsync(req.Content);

            senderRef.Tell(new Records.AgentResponse(response.Text ?? "Operazione contrattuale completata."));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error executing contract agent");
            senderRef.Tell(new Records.AgentResponse("Errore nell'operazione. Contatti l'ufficio contratti."));
        }
    }
}