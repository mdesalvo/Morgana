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
/// </summary>
/// <remarks>
/// <para>The one agent of the harness's hijacking domain: two MCP servers and two colleagues at other
/// installations are named in code, on the attributes below, so no configuration could stand this
/// topology up on its own.</para>
///
/// <para>The addresses of the MCP servers repeat <c>PoisonedSourceHost</c>, which serves them: an
/// attribute argument cannot come from configuration.</para>
/// </remarks>
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
