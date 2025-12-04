using Akka.Actor;
using Morgana.Adapters;
using Morgana.Messages;
using Morgana.Interfaces;
using Microsoft.Agents.AI;

namespace Morgana.Agents.Executors;

public class HardwareTroubleshootingExecutorAgent : ReceiveActor
{
    private readonly AIAgent _agent;
    private readonly ILogger<HardwareTroubleshootingExecutorAgent> _logger;

    public HardwareTroubleshootingExecutorAgent(ILLMService llmService, ILogger<HardwareTroubleshootingExecutorAgent> logger)
    {
        _logger = logger;

        var adapter = new AgentExecutorAdapter(llmService.GetChatClient());
        _agent = adapter.CreateTroubleshootingAgent();

        ReceiveAsync<ExecuteRequest>(ExecuteTroubleshooting);
    }

    private async Task ExecuteTroubleshooting(ExecuteRequest req)
    {
        try
        {
            _logger.LogInformation($"Executing troubleshooting for user {req.UserId}");

            var thread = _agent.GetNewThread();
            var response = await _agent.RunAsync(req.Content, thread: thread);

            Sender.Tell(new ExecuteResponse(response.Text ?? "Diagnostica completata."));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing troubleshooting agent");
            Sender.Tell(new ExecuteResponse("Errore durante la diagnostica. Contatti il supporto tecnico."));
        }
    }
}