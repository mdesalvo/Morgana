using Microsoft.Extensions.Logging;
using Morgana.AI.Abstractions;
using Morgana.AI.Adapters;
using Morgana.AI.Attributes;
using Morgana.AI.Interfaces;

namespace Morgana.AI.Examples.Agents;

/// <summary>
/// Example agent demonstrating MonkeyMCP server integration.
/// This agent can retrieve information about monkeys from a public MCP server.
/// 
/// Available tools:
/// - MonkeyMCP_get_all_monkeys: Returns list of all monkeys
/// - MonkeyMCP_find_monkey_by_name: Finds specific monkey by name
/// 
/// Test queries:
/// - "Get me a list of all monkeys"
/// - "Find information about the Baboon"
/// - "Tell me about monkeys from Africa"
/// </summary>
[HandlesIntent("monkeys")]
[UsesMCPServers("MonkeyMCP")]
public class MonkeyExampleAgent : MorganaAgent
{
    public MonkeyExampleAgent(
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