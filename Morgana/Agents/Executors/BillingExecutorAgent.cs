using Akka.Actor;
using Morgana.Adapters;
using Morgana.Messages;
using Morgana.Interfaces;
using Microsoft.Agents.AI;

namespace Morgana.Agents.Executors;

public class BillingExecutorAgent : MorganaAgent
{
    private readonly AIAgent aiAgent;
    private readonly ILogger<BillingExecutorAgent> logger;

    public BillingExecutorAgent(string conversationId, string userId, ILLMService llmService,
        IStorageService storageService, ILogger<BillingExecutorAgent> logger) : base(conversationId, userId)
    {
        this.logger = logger;

        AgentExecutorAdapter adapter = new AgentExecutorAdapter(llmService.GetChatClient());
        aiAgent = adapter.CreateBillingAgent(storageService);

        ReceiveAsync<ExecuteRequest>(ExecuteBillingAsync);
    }

    private async Task ExecuteBillingAsync(ExecuteRequest req)
    {
        IActorRef originalSender = Sender;

        try
        {
            logger.LogInformation($"Executing billing request for user {req.UserId}");

            AgentThread thread = aiAgent.GetNewThread();
            AgentRunResponse response = await aiAgent.RunAsync(req.Content, thread: thread);

            originalSender.Tell(new ExecuteResponse(response.Text ?? "Operazione completata."));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error executing billing agent");
            originalSender.Tell(new ExecuteResponse("Si è verificato un errore. La preghiamo di riprovare."));
        }
    }
}