using Akka.Actor;
using Morgana.Adapters;
using Morgana.Messages;
using Morgana.Interfaces;
using Microsoft.Agents.AI;

namespace Morgana.Agents.Executors;

public class ContractCancellationExecutorAgent : MorganaAgent
{
    private readonly AIAgent aiAgent;
    private readonly ILogger<ContractCancellationExecutorAgent> logger;

    public ContractCancellationExecutorAgent(string conversationId, string userId, ILLMService llmService,
        IStorageService storageService, ILogger<ContractCancellationExecutorAgent> logger) : base(conversationId, userId)
    {
        this.logger = logger;

        AgentExecutorAdapter adapter = new AgentExecutorAdapter(llmService.GetChatClient());
        aiAgent = adapter.CreateContractAgent(storageService);

        ReceiveAsync<ExecuteRequest>(ExecuteCancellationAsync);
    }

    private async Task ExecuteCancellationAsync(ExecuteRequest req)
    {
        IActorRef originalSender = Sender;

        try
        {
            logger.LogInformation($"Executing contract operation for user {req.UserId}");

            AgentThread thread = aiAgent.GetNewThread();
            AgentRunResponse response = await aiAgent.RunAsync(req.Content, thread: thread);

            originalSender.Tell(new ExecuteResponse(response.Text ?? "Operazione contrattuale completata."));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error executing contract agent");
            originalSender.Tell(new ExecuteResponse("Errore nell'operazione. Contatti l'ufficio contratti."));
        }
    }
}