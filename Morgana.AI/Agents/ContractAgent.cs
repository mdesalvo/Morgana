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
        IPromptResolverService promptResolverService,
        ILogger<ContractAgent> logger) : base(conversationId, llmService, promptResolverService, logger)
    {
        AgentAdapter adapter = new AgentAdapter(llmService.GetChatClient(), promptResolverService);
        aiAgent = adapter.CreateContractAgent();

        ReceiveAsync<Records.AgentRequest>(ExecuteAgentAsync);
    }
}