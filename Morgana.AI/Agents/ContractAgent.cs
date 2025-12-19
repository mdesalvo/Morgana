using Microsoft.Extensions.Logging;
using Morgana.AI.Abstractions;
using Morgana.AI.Adapters;
using Morgana.AI.Attributes;
using Morgana.AI.Interfaces;

namespace Morgana.AI.Agents;

[HandlesIntent("contract")]
public class ContractAgent : MorganaAgent
{
    public ContractAgent(
        string conversationId,
        ILLMService llmService,
        IPromptResolverService promptResolverService,
        ILogger<ContractAgent> logger) : base(conversationId, llmService, promptResolverService, logger)
    {
        AgentAdapter adapter = new AgentAdapter(llmService.GetChatClient(), promptResolverService, logger);
        aiAgent = adapter.CreateContractAgent(AgentContext);

        ReceiveAsync<Records.AgentRequest>(ExecuteAgentAsync);
    }
}