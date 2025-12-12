using Microsoft.Extensions.Logging;
using Morgana.AI.Abstractions;
using Morgana.AI.Adapters;
using Morgana.AI.Interfaces;

namespace Morgana.AI.Agents;

public class ContractAgent : MorganaAgent
{
    public ContractAgent(
        string conversationId,
        ILLMService llmService,
        ILogger<ContractAgent> logger) : base(conversationId, llmService, logger)
    {
        AgentAdapter adapter = new AgentAdapter(llmService.GetChatClient());
        aiAgent = adapter.CreateContractAgent();

        ReceiveAsync<Records.AgentRequest>(ExecuteAgentAsync);
    }
}