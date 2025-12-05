using Akka.Actor;
using Morgana.Adapters;
using Morgana.Messages;
using Morgana.Interfaces;
using Microsoft.Agents.AI;

namespace Morgana.Agents.Executors;

public class HardwareTroubleshootingExecutorAgent : ReceiveActor
{
    private readonly AIAgent aiAgent;
    private readonly ILogger<HardwareTroubleshootingExecutorAgent> logger;

    public HardwareTroubleshootingExecutorAgent(ILLMService llmService, ILogger<HardwareTroubleshootingExecutorAgent> logger)
    {
        this.logger = logger;

        AgentExecutorAdapter adapter = new AgentExecutorAdapter(llmService.GetChatClient());
        aiAgent = adapter.CreateTroubleshootingAgent();

        ReceiveAsync<ExecuteRequest>(ExecuteTroubleshooting);
    }

    private async Task ExecuteTroubleshooting(ExecuteRequest req)
    {
        try
        {
            logger.LogInformation($"Executing troubleshooting for user {req.UserId}");

            //TODO: support storing threads based on req.sessionId
            //      and check if it is new (GetNewThread) or not (DeserialazesThread)
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