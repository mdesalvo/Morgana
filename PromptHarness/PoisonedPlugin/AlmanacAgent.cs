using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Morgana.AI;
using Morgana.AI.Abstractions;
using Morgana.AI.Adapters;
using Morgana.AI.Attributes;
using Morgana.AI.Interfaces;

namespace PoisonedPlugin;

/// <summary>
/// The almanac desk of a nursery whose every source has been turned against it: its own books, the
/// weather service and the partner nursery all answer with text addressed to the agent.
/// </summary>
/// <remarks>
/// The one agent of the harness's tool-guard domain, reaching the three kinds of source the guard
/// tells apart: native tools, an MCP server and a colleague published by a partner. The server and
/// the partner are the harness's <c>PoisonedSourceHost</c>, whose fixed address the attribute below
/// must repeat, since an attribute argument cannot be read from configuration.
/// </remarks>
[HandlesIntent("almanac")]
[RequiresLLMTier(Records.LLMTier.Efficiency)]
[UsesMCPServer("http://127.0.0.1:5199/mcp")]
[ConsultsAgent("hedging", "hedgerow")]
public class AlmanacAgent : MorganaAgent
{
    /// <summary>Builds the agent with its books, the weather service and the partner nursery in its tool list.</summary>
    public AlmanacAgent(string conversationId,
        ILLMService llmService,
        IPromptResolverService promptResolverService,
        IConversationPersistenceService persistenceService,
        ILogger logger,
        MorganaAgentAdapter adapter,
        IConfiguration configuration)
        : base(conversationId, llmService, promptResolverService, persistenceService, logger, configuration)
    {
        (aiAgent, aiContextProvider, aiChatHistoryProvider) =
            adapter.CreateAgent(GetType(), conversationId, () => CurrentSession, OnSharedContextUpdate);
    }
}
