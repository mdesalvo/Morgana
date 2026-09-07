using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Morgana.AI;
using Morgana.AI.Abstractions;
using Morgana.AI.Adapters;
using Morgana.AI.Attributes;
using Morgana.AI.Interfaces;

namespace FederationPlugin;

/// <summary>
/// The dispatch desk of a nursery whose greenhouse stands at another site: it answers for deliveries
/// and holds no books about plants at all, so every question about stock is its colleague's.
/// </summary>
/// <remarks>
/// <para>The one agent of the harness's federation domain and the reason that domain exists: a
/// colleague published by another installation is named in code, on the attribute below, so no
/// configuration could stand this topology up on its own.</para>
///
/// <para>Deliberately toolless. A desk with books of its own could answer from them and a turn that
/// consulted nobody would look exactly like a turn whose consultation silently failed.</para>
/// </remarks>
[HandlesIntent("dispatch")]
[RequiresLLMTier(Records.LLMTier.Efficiency)]
[ConsultsAgent("inventory", "annex")]
public class DispatchAgent : MorganaAgent
{
    /// <summary>Builds the desk, with its colleague at the annex already in its tool list.</summary>
    public DispatchAgent(string conversationId,
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
