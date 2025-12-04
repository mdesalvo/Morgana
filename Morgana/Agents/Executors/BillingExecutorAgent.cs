using Akka.Actor;
using Morgana.Adapters;
using Morgana.Messages;
using Morgana.Interfaces;
using Microsoft.Agents.AI;

namespace Morgana.Agents.Executors;

public class BillingExecutorAgent : ReceiveActor
{
    private readonly AIAgent _agent;
    private readonly ILogger<BillingExecutorAgent> _logger;

    public BillingExecutorAgent(ILLMService llmService, IStorageService storageService, ILogger<BillingExecutorAgent> logger)
    {
        _logger = logger;

        var adapter = new AgentExecutorAdapter(llmService.GetChatClient());
        _agent = adapter.CreateBillingAgent(storageService);

        ReceiveAsync<ExecuteRequest>(ExecuteBilling);
    }

    private async Task ExecuteBilling(ExecuteRequest req)
    {
        try
        {
            _logger.LogInformation($"Executing billing request for user {req.UserId}");

            // Crea thread per mantenere contesto conversazione
            var thread = _agent.GetNewThread();

            // Esegui agente con tools
            var response = await _agent.RunAsync(req.Content, thread: thread);

            Sender.Tell(new ExecuteResponse(response.Text ?? "Operazione completata."));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing billing agent");
            Sender.Tell(new ExecuteResponse("Si è verificato un errore. La preghiamo di riprovare."));
        }
    }
}