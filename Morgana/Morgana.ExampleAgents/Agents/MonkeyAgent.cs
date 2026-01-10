using Microsoft.Extensions.Logging;
using Morgana.AgentsFramework.Abstractions;
using Morgana.AgentsFramework.Adapters;
using Morgana.AgentsFramework.Attributes;
using Morgana.Foundations;
using Morgana.Foundations.Interfaces;

namespace Morgana.ExampleAgents.Agents;

/// <summary>
/// Example agent demonstrating MonkeyMCP server integration.
/// This agent can retrieve information about monkeys from a public MCP server.
/// 
/// Available tools:
/// - get_all_monkeys: Returns list of all monkeys
/// - find_monkey_by_name: Finds specific monkey by name
/// 
/// Test queries:
/// - "Get me a list of all monkeys"
/// - "Find information about the Baboon"
/// - "Tell me about monkeys from Africa"
/// </summary>
[HandlesIntent("monkeys")]
[UsesMCPServers("MonkeyMCP")]
public class MonkeyAgent : MorganaAgent
{
    public MonkeyAgent(
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