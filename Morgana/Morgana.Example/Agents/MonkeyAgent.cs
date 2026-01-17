using Microsoft.Extensions.Logging;
using Morgana.Framework.Abstractions;
using Morgana.Framework.Adapters;
using Morgana.Framework.Attributes;
using Morgana.Framework.Interfaces;

namespace Morgana.Example.Agents;

/// <summary>
/// <para>
/// Example agent demonstrating MonkeyMCP server integration.
/// This agent can retrieve information about monkeys from a public MCP server.
/// </para>
/// <para>
/// Available tools:
/// - get_all_monkeys: Returns list of all monkeys
/// - find_monkey_by_name: Finds specific monkey by name
/// </para>
/// <para>
/// Test queries:
/// - "Get me a list of all monkeys"
/// - "Find information about the Baboon"
/// - "Tell me about monkeys from Africa"
/// </para>
/// </summary>
[HandlesIntent("monkeys")]
[UsesMCPServers("MonkeyMCP")]
public class MonkeyAgent : MorganaAgent
{
    public MonkeyAgent(
        string conversationId,
        ILLMService llmService,
        IPromptResolverService promptResolverService,
        IConversationPersistenceService conversationPersistenceService,
        ILogger logger,
        MorganaAgentAdapter morganaAgentAdapter) : base(conversationId, llmService, promptResolverService, conversationPersistenceService, logger)

    {
        (aiAgent, contextProvider) = morganaAgentAdapter.CreateAgent(GetType(), conversationId, OnSharedContextUpdate);
    }
}