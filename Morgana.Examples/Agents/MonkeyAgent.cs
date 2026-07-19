using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Morgana.AI.Abstractions;
using Morgana.AI.Adapters;
using Morgana.AI.Attributes;
using Morgana.AI.Interfaces;
using static Morgana.AI.Records;

namespace Morgana.Examples.Agents;

/// <summary>
/// Example agent demonstrating MCP server integration via direct URI declaration.
/// Retrieves information about monkeys from the public MonkeyMCP server.
/// </summary>
[HandlesIntent("monkeys")]
[RequiresLLMTier(LLMTier.Efficiency)]
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