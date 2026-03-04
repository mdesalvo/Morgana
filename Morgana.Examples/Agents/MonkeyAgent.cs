using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Morgana.Framework.Abstractions;
using Morgana.Framework.Adapters;
using Morgana.Framework.Attributes;
using Morgana.Framework.Interfaces;

namespace Morgana.Examples.Agents;

/// <summary>
/// Example agent demonstrating MCP server integration via direct URI declaration.
/// Retrieves information about monkeys from the public MonkeyMCP server.
/// </summary>
/// <remarks>
/// <para><strong>Automatically acquired tools:</strong></para>
/// <list type="bullet">
/// <item>get_monkeys — returns the full list of monkeys</item>
/// <item>get_monkey(name) — finds a specific monkey by name</item>
/// <item>get_monkey_journey(name) — returns the journey of a specific monkey</item>
/// <item>get_all_monkey_journeys — returns all monkey journeys</item>
/// <item>get_monkey_business — returns monkey business data 🐵</item>
/// </list>
/// <para><strong>Test queries:</strong></para>
/// <list type="bullet">
/// <item>"Get me a list of all monkeys"</item>
/// <item>"Find information about the Baboon"</item>
/// <item>"Tell me about monkeys from Africa"</item>
/// </list>
/// </remarks>
[HandlesIntent("monkeys")]
[UsesMCPServer("https://func-monkeymcp-3t4eixuap5dfm.azurewebsites.net/")]
public class MonkeyAgent : MorganaAgent
{
    public MonkeyAgent(
        string conversationId,
        ILLMService llmService,
        IPromptResolverService promptResolverService,
        IConversationPersistenceService conversationPersistenceService,
        ILogger logger,
        MorganaAgentAdapter morganaAgentAdapter,
        IConfiguration configuration) : base(conversationId, llmService, promptResolverService, conversationPersistenceService, logger, configuration)
    {
        (aiAgent, aiContextProvider, aiChatHistoryProvider)
            = morganaAgentAdapter.CreateAgent(GetType(), conversationId, () => CurrentSession, OnSharedContextUpdate);
    }
}