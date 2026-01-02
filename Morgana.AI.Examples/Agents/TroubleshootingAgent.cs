using Microsoft.Extensions.Logging;
using Morgana.AI.Abstractions;
using Morgana.AI.Adapters;
using Morgana.AI.Attributes;
using Morgana.AI.Interfaces;
using Morgana.AI.Providers;

namespace Morgana.AI.Examples.Agents;

[HandlesIntent("troubleshooting")]
[UsesMCPServers("HardwareCatalog", "SecurityCatalog")]
public class TroubleshootingAgent : MorganaAgent
{
    public TroubleshootingAgent(
        string conversationId,
        ILLMService llmService,
        IPromptResolverService promptResolverService,
        ILogger logger,
        AgentAdapter agentAdapter) : base(conversationId, llmService, promptResolverService, logger)
    {
        // Generic agent creation - automatically loads MCP tools from UsesMCPServers attribute
        (aiAgent, contextProvider) = agentAdapter.CreateAgent(GetType(), OnSharedContextUpdate);

        ReceiveAsync<Records.AgentRequest>(ExecuteAgentAsync);
    }
}