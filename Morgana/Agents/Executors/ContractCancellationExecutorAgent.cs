using Akka.Actor;
using Morgana.Adapters;
using Morgana.Messages;
using Morgana.Interfaces;
using Microsoft.Agents.AI;

namespace Morgana.Agents.Executors;

public class ContractCancellationExecutorAgent : ReceiveActor
{
    private readonly AIAgent aiAgent;
    private readonly ILogger<ContractCancellationExecutorAgent> logger;

    public ContractCancellationExecutorAgent(ILLMService llmService, IStorageService storageService, ILogger<ContractCancellationExecutorAgent> logger)
    {
        this.logger = logger;

        AgentExecutorAdapter adapter = new AgentExecutorAdapter(llmService.GetChatClient());
        aiAgent = adapter.CreateContractAgent(storageService);

        ReceiveAsync<ExecuteRequest>(ExecuteCancellation);
    }

    private async Task ExecuteCancellation(ExecuteRequest req)
    {
        try
        {
            logger.LogInformation($"Executing contract operation for user {req.UserId}");

            //TODO: support storing threads based on req.sessionId
            //      and check if it is new (GetNewThread) or not (DeserialazesThread)
            AgentThread thread = aiAgent.GetNewThread();
            AgentRunResponse response = await aiAgent.RunAsync(req.Content, thread: thread);

            Sender.Tell(new ExecuteResponse(response.Text ?? "Operazione contrattuale completata."));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error executing contract agent");
            Sender.Tell(new ExecuteResponse("Errore nell'operazione. Contatti l'ufficio contratti."));
        }
    }
}