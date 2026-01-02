using Microsoft.Extensions.Logging;
using Morgana.AI.Abstractions;
using Morgana.AI.Adapters;
using Morgana.AI.Attributes;
using Morgana.AI.Interfaces;
using Morgana.AI.Providers;

namespace Morgana.AI.Examples.Agents;

[HandlesIntent("contract")]
// No MCP servers needed - uses only native tools
public class ContractAgent : MorganaAgent
{
    public ContractAgent(
        string conversationId,
        ILLMService llmService,
        IPromptResolverService promptResolverService,
        ILogger logger,
        AgentAdapter agentAdapter) : base(conversationId, llmService, promptResolverService, logger)
    {
        // Generic agent creation - no MCP tools loaded (no UsesMCPServers attribute)
        (aiAgent, contextProvider) = agentAdapter.CreateAgent(GetType(), OnSharedContextUpdate);

        ReceiveAsync<Records.AgentRequest>(ExecuteAgentAsync);
    }
}