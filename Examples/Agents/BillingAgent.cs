using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Morgana.AI.Abstractions;
using Morgana.AI.Adapters;
using Morgana.AI.Attributes;
using Morgana.AI.Interfaces;
using static Morgana.AI.Records;

namespace Examples.Agents;

/// <summary>The billing clerk of The Greenhouse &amp; Nursery, who reads the books every other agent writes and consults the inventory.</summary>
[HandlesIntent("billing")]
[ConsultsAgent("inventory")]
[RequiresLLMTier(LLMTier.Efficiency)]
public class BillingAgent : MorganaAgent
{
    /// <summary>Hands the adapter to the base, which builds the agent from its prompt, its tools and its colleagues.</summary>
    public BillingAgent(
        string conversationId,
        ILanguageModelService llmService,
        IPromptResolverService promptResolverService,
        IConversationPersistenceService conversationPersistenceService,
        ILogger logger,
        MorganaAgentAdapter morganaAgentAdapter,
        IConfiguration configuration) : base(conversationId, llmService, promptResolverService, conversationPersistenceService, logger, morganaAgentAdapter, configuration) { }
}