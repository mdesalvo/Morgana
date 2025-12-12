using Akka.Actor;
using Morgana.Adapters;
using Morgana.Interfaces;
using Microsoft.Agents.AI;
using static Morgana.Records;
using Morgana.Actors;

namespace Morgana.AI.Agents;

public class ContractCancellationAgent : MorganaActor
{
    private readonly AIAgent aiAgent;
    private readonly ILogger<ContractCancellationAgent> logger;

    public ContractCancellationAgent(string conversationId, ILLMService llmService,
        ILogger<ContractCancellationAgent> logger) : base(conversationId)
    {
        this.logger = logger;

        AgentAdapter adapter = new AgentAdapter(llmService.GetChatClient());
        aiAgent = adapter.CreateContractAgent();

        ReceiveAsync<ExecuteRequest>(ExecuteCancellationAsync);
    }

    private async Task ExecuteCancellationAsync(ExecuteRequest req)
    {
        IActorRef senderRef = Sender;

        try
        {
            logger.LogInformation($"Executing contract operation for conversation {req.ConversationId}");

            AgentRunResponse response = await aiAgent.RunAsync(req.Content);

            senderRef.Tell(new ExecuteResponse(response.Text ?? "Operazione contrattuale completata."));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error executing contract agent");
            senderRef.Tell(new ExecuteResponse("Errore nell'operazione. Contatti l'ufficio contratti."));
        }
    }
}