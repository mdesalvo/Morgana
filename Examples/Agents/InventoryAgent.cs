using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Morgana.AI.Abstractions;
using Morgana.AI.Adapters;
using Morgana.AI.Attributes;
using Morgana.AI.Interfaces;
using static Morgana.AI.Records;

namespace Examples.Agents;

/// <summary>The shop assistant of The Greenhouse &amp; Nursery, who answers on stock and confirms orders.</summary>
[HandlesIntent("inventory")]
[RequiresLLMTier(LLMTier.Efficiency)]
public class InventoryAgent : MorganaAgent
{
    /// <summary>Hands the adapter to the base, which builds the agent from its prompt, its tools and its colleagues.</summary>
    public InventoryAgent(
        string conversationId,
        ILanguageModelService llmService,
        IPromptResolverService promptResolverService,
        IConversationPersistenceService conversationPersistenceService,
        ILogger logger,
        MorganaAgentAdapter morganaAgentAdapter,
        IConfiguration configuration) : base(conversationId, llmService, promptResolverService, conversationPersistenceService, logger, morganaAgentAdapter, configuration) { }
}