using Akka.Actor;
using Morgana.Adapters;
using Morgana.Messages;
using Morgana.Interfaces;
using Microsoft.Agents.AI;

namespace Morgana.Agents.Executors;

public class BillingExecutorAgent : ReceiveActor
{
    private readonly AIAgent aiAgent;
    private readonly ILogger<BillingExecutorAgent> logger;

    public BillingExecutorAgent(ILLMService llmService, IStorageService storageService, ILogger<BillingExecutorAgent> logger)
    {
        this.logger = logger;

        AgentExecutorAdapter adapter = new AgentExecutorAdapter(llmService.GetChatClient());
        aiAgent = adapter.CreateBillingAgent(storageService);

        ReceiveAsync<ExecuteRequest>(ExecuteBilling);
    }

    private async Task ExecuteBilling(ExecuteRequest req)
    {
        try
        {
            logger.LogInformation($"Executing billing request for user {req.UserId}");

            //TODO: support storing threads based on req.sessionId
            //      and check if it is new (GetNewThread) or not (DeserialazesThread)
            AgentThread thread = aiAgent.GetNewThread();
            AgentRunResponse response = await aiAgent.RunAsync(req.Content, thread: thread);

            Sender.Tell(new ExecuteResponse(response.Text ?? "Operazione completata."));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error executing billing agent");
            Sender.Tell(new ExecuteResponse("Si è verificato un errore. La preghiamo di riprovare."));
        }
    }
}