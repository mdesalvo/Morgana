using Microsoft.Extensions.Logging;
using Morgana.Agents.Abstractions;
using Morgana.Agents.Adapters;
using Morgana.Agents.Attributes;
using Morgana.Foundations;
using Morgana.Foundations.Interfaces;

namespace Morgana.Example.Agents;

[HandlesIntent("billing")]
public class BillingAgent : MorganaAgent
{
    public BillingAgent(
        string conversationId,
        ILLMService llmService,
        IPromptResolverService promptResolverService,
        ILogger logger,
        MorganaAgentAdapter morganaAgentAdapter) : base(conversationId, llmService, promptResolverService, logger)
    {
        (aiAgent, contextProvider) = morganaAgentAdapter.CreateAgent(GetType(), OnSharedContextUpdate);

        ReceiveAsync<Records.AgentRequest>(ExecuteAgentAsync);
    }
}