using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Morgana.AI.Abstractions;
using Morgana.AI.Adapters;
using Morgana.AI.Attributes;
using Morgana.AI.Interfaces;
using static Morgana.AI.Records;

namespace Examples.Agents;

/// <summary>
/// The head gardener of The Greenhouse &amp; Nursery, whose tools all come from the GardeningMCP server.
/// </summary>
[HandlesIntent("gardening")]
[RequiresLLMTier(LLMTier.Economy)]
[UsesMCPServer(GardeningAgent.AlmanacServerAddress)]
public class GardeningAgent : MorganaAgent
{
    /// <summary>The address where the GardeningMCP server answers, shared by this agent and the server.</summary>
    public const string AlmanacServerAddress = "http://localhost:5006/mcp";

    public GardeningAgent(
        string conversationId,
        ILanguageModelService llmService,
        IPromptResolverService promptResolverService,
        IConversationPersistenceService conversationPersistenceService,
        ILogger logger,
        MorganaAgentAdapter morganaAgentAdapter,
        IConfiguration configuration) : base(conversationId, llmService, promptResolverService, conversationPersistenceService, logger, morganaAgentAdapter, configuration) { }
}