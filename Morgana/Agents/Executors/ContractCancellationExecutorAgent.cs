using Akka.Actor;
using Morgana.Adapters;
using Morgana.Interfaces;
using Microsoft.Agents.AI;
using static Morgana.Records;

namespace Morgana.Agents.Executors;

public class ContractCancellationExecutorAgent : MorganaAgent
{
    private readonly AIAgent aiAgent;
    private readonly ILogger<ContractCancellationExecutorAgent> logger;

    public ContractCancellationExecutorAgent(string conversationId, string userId, ILLMService llmService,
        ILogger<ContractCancellationExecutorAgent> logger) : base(conversationId, userId)
    {
        this.logger = logger;

        AgentExecutorAdapter adapter = new AgentExecutorAdapter(llmService.GetChatClient());
        aiAgent = adapter.CreateContractAgent();

        ReceiveAsync<ExecuteRequest>(ExecuteCancellationAsync);
    }

    private async Task ExecuteCancellationAsync(ExecuteRequest req)
    {
        IActorRef originalSender = Sender;

        try
        {
            logger.LogInformation($"Executing contract operation for user {req.UserId}");

            AgentRunResponse response = await aiAgent.RunAsync(req.Content);

            originalSender.Tell(new ExecuteResponse(response.Text ?? "Operazione contrattuale completata."));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error executing contract agent");
            originalSender.Tell(new ExecuteResponse("Errore nell'operazione. Contatti l'ufficio contratti."));
        }
    }
}