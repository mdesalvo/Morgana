using Microsoft.Extensions.Logging;
using Morgana.AI.Abstractions;
using Morgana.AI.Adapters;
using Morgana.AI.Attributes;
using Morgana.AI.Interfaces;
using Morgana.AI.Providers;

namespace Morgana.AI.Agents;

[HandlesIntent("billing")]
public class BillingAgent : MorganaAgent
{
    public BillingAgent(
        string conversationId,
        ILLMService llmService,
        IPromptResolverService promptResolverService,
        ILogger<BillingAgent> logger,
        ILogger<MorganaContextProvider> contextProviderLogger,
        AgentAdapter agentAdapter) : base(conversationId, llmService, promptResolverService, logger)
    {
        (aiAgent, contextProvider) = agentAdapter.CreateBillingAgent(OnSharedContextUpdate);

        ReceiveAsync<Records.AgentRequest>(ExecuteAgentAsync);
    }
}