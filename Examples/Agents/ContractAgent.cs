using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Morgana.AI.Abstractions;
using Morgana.AI.Adapters;
using Morgana.AI.Attributes;
using Morgana.AI.Interfaces;
using static Morgana.AI.Records;

namespace Examples.Agents;

/// <summary>The plans clerk of The Greenhouse &amp; Nursery, who enrols a customer in a care plan and consults billing.</summary>
[HandlesIntent("contract")]
[ConsultsAgent("billing")]
[RequiresLLMTier(LLMTier.Efficiency)]
public class ContractAgent : MorganaAgent
{
    /// <summary>Hands the adapter to the base, which builds the agent from its prompt, its tools and its colleagues.</summary>
    public ContractAgent(
        string conversationId,
        ILanguageModelService llmService,
        IPromptResolverService promptResolverService,
        IConversationPersistenceService conversationPersistenceService,
        ILogger logger,
        MorganaAgentAdapter morganaAgentAdapter,
        IConfiguration configuration) : base(conversationId, llmService, promptResolverService, conversationPersistenceService, logger, morganaAgentAdapter, configuration) { }
}