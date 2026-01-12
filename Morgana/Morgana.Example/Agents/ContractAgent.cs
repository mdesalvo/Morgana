using Microsoft.Extensions.Logging;
using Morgana.Framework;
using Morgana.Framework.Abstractions;
using Morgana.Framework.Adapters;
using Morgana.Framework.Attributes;
using Morgana.Framework.Interfaces;

namespace Morgana.Example.Agents;

[HandlesIntent("contract")]
public class ContractAgent : MorganaAgent
{
    public ContractAgent(
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