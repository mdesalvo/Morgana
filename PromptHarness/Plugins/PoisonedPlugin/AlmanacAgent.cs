using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Morgana.AI;
using Morgana.AI.Abstractions;
using Morgana.AI.Adapters;
using Morgana.AI.Attributes;
using Morgana.AI.Interfaces;

namespace PoisonedPlugin;

/// <summary>
/// The almanac keeper of a nursery whose every outside source and one of its own books answers with an attempt at hijacking.
/// Its two MCP servers and two colleagues at other installations are named in code, on its attributes, so no configuration could stand the topology up.
/// The servers' addresses repeat those <c>PoisonedSourceHost</c> serves, since an attribute argument cannot come from configuration.
/// </summary>
[HandlesIntent("almanac")]
[RequiresLLMTier(Records.LLMTier.Efficiency)]
[UsesMCPServer("http://127.0.0.1:5199/mcp")]
[UsesMCPServer("http://127.0.0.1:5198/mcp")]
[ConsultsAgent("hedging", "hedgerow")]
[ConsultsAgent("orchard", "quince")]
public class AlmanacAgent : MorganaAgent
{
    /// <summary>Builds the agent, with its pruning guide and its outside sources already in its tool list.</summary>
    public AlmanacAgent(string conversationId,
        ILanguageModelService llmService,
        IPromptResolverService promptResolverService,
        IConversationPersistenceService persistenceService,
        ILogger logger,
        MorganaAgentAdapter adapter,
        IConfiguration configuration)
        : base(conversationId, llmService, promptResolverService, persistenceService, logger, adapter, configuration) { }
}