using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Morgana.AI;
using Morgana.AI.Abstractions;
using Morgana.AI.Adapters;
using Morgana.AI.Attributes;
using Morgana.AI.Interfaces;

namespace FederationPlugin;

/// <summary>
/// The dispatch agent of a nursery whose greenhouse stands at another site, holding no books about plants, so every question about stock is its colleague's.
/// It is the one agent of the federation domain because a colleague at another installation is named in code, on its attribute.
/// It has no tools: with books of its own, a turn that consulted nobody would look exactly like one whose consultation silently failed.
/// </summary>
[HandlesIntent("dispatch")]
[RequiresLLMTier(Records.LLMTier.Efficiency)]
[ConsultsAgent("inventory", "annex")]
public class DispatchAgent : MorganaAgent
{
    /// <summary>Builds the agent, with its colleague at the annex already in its tool list.</summary>
    public DispatchAgent(string conversationId,
        ILanguageModelService llmService,
        IPromptResolverService promptResolverService,
        IConversationPersistenceService persistenceService,
        ILogger logger,
        MorganaAgentAdapter adapter,
        IConfiguration configuration)
        : base(conversationId, llmService, promptResolverService, persistenceService, logger, adapter, configuration) { }
}