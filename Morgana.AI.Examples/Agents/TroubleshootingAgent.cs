using Microsoft.Extensions.Logging;
using Morgana.AI.Abstractions;
using Morgana.AI.Adapters;
using Morgana.AI.Attributes;
using Morgana.AI.Interfaces;

namespace Morgana.AI.Examples.Agents;

[HandlesIntent("troubleshooting")]
public class TroubleshootingAgent : MorganaAgent
{
    public TroubleshootingAgent(
        string conversationId,
        ILLMService llmService,
        IPromptResolverService promptResolverService,
        ILogger logger,
        AgentAdapter agentAdapter) : base(conversationId, llmService, promptResolverService, logger)
    {
        (aiAgent, contextProvider) = agentAdapter.CreateAgent(GetType(), OnSharedContextUpdate);

        ReceiveAsync<Records.AgentRequest>(ExecuteAgentAsync);
    }
}