using Akka.Actor;
using Morgana.Adapters;
using Morgana.Messages;
using Morgana.Interfaces;
using Microsoft.Agents.AI;

namespace Morgana.Agents.Executors;

public class ContractCancellationExecutorAgent : ReceiveActor
{
    private readonly AIAgent _agent;
    private readonly ILogger<ContractCancellationExecutorAgent> _logger;

    public ContractCancellationExecutorAgent(ILLMService llmService, IStorageService storageService, ILogger<ContractCancellationExecutorAgent> logger)
    {
        _logger = logger;

        var adapter = new AgentExecutorAdapter(llmService.GetChatClient());
        _agent = adapter.CreateContractAgent(storageService);

        ReceiveAsync<ExecuteRequest>(ExecuteCancellation);
    }

    private async Task ExecuteCancellation(ExecuteRequest req)
    {
        try
        {
            _logger.LogInformation($"Executing contract operation for user {req.UserId}");

            var thread = _agent.GetNewThread();
            var response = await _agent.RunAsync(req.Content, thread: thread);

            Sender.Tell(new ExecuteResponse(response.Text ?? "Operazione contrattuale completata."));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing contract agent");
            Sender.Tell(new ExecuteResponse("Errore nell'operazione. Contatti l'ufficio contratti."));
        }
    }
}