using Akka.Actor;
using Morgana.Adapters;
using Morgana.Messages;
using Morgana.Interfaces;
using Microsoft.Agents.AI;

namespace Morgana.Agents.Executors;

public class HardwareTroubleshootingExecutorAgent : MorganaAgent
{
    private readonly AIAgent aiAgent;
    private readonly ILogger<HardwareTroubleshootingExecutorAgent> logger;

    public HardwareTroubleshootingExecutorAgent(string conversationId, string userId, ILLMService llmService,
        ILogger<HardwareTroubleshootingExecutorAgent> logger) : base(conversationId, userId)
    {
        this.logger = logger;

        AgentExecutorAdapter adapter = new AgentExecutorAdapter(llmService.GetChatClient());
        aiAgent = adapter.CreateTroubleshootingAgent();

        ReceiveAsync<ExecuteRequest>(ExecuteTroubleshootingAsync);
    }

    private async Task ExecuteTroubleshootingAsync(ExecuteRequest req)
    {
        IActorRef originalSender = Sender;

        try
        {
            logger.LogInformation($"Executing troubleshooting for user {req.UserId}");

            AgentThread thread = aiAgent.GetNewThread();
            AgentRunResponse response = await aiAgent.RunAsync(req.Content, thread: thread);

            Sender.Tell(new ExecuteResponse(response.Text ?? "Diagnostica completata."));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error executing troubleshooting agent");
            Sender.Tell(new ExecuteResponse("Errore durante la diagnostica. Contatti il supporto tecnico."));
        }
    }
}