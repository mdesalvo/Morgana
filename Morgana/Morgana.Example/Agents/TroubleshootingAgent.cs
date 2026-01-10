using Microsoft.Extensions.Logging;
using Morgana.Agents.Abstractions;
using Morgana.Agents.Adapters;
using Morgana.Agents.Attributes;
using Morgana.Foundations;
using Morgana.Foundations.Interfaces;

namespace Morgana.Example.Agents;

[HandlesIntent("troubleshooting")]
public class TroubleshootingAgent : MorganaAgent
{
    public TroubleshootingAgent(
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