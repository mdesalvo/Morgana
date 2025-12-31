using Microsoft.Extensions.Logging;
using Morgana.AI.Abstractions;
using Morgana.AI.Adapters;
using Morgana.AI.Attributes;
using Morgana.AI.Interfaces;
using Morgana.AI.Providers;

namespace Morgana.AI.Agents;

[HandlesIntent("contract")]
public class ContractAgent : MorganaAgent
{
    public ContractAgent(
        string conversationId,
        ILLMService llmService,
        IPromptResolverService promptResolverService,
        ILogger<ContractAgent> logger,
        ILogger<MorganaContextProvider> contextProviderLogger,
        AgentAdapter agentAdapter) : base(conversationId, llmService, promptResolverService, logger)
    {
        (aiAgent, contextProvider) = agentAdapter.CreateContractAgent(OnSharedContextUpdate);

        ReceiveAsync<Records.AgentRequest>(ExecuteAgentAsync);
    }
}