using A2A;
using Microsoft.Agents.AI;

namespace Morgana.AI.Interfaces;

/// <summary>
/// Both halves of A2A discovery: the <see cref="AgentCard"/> an agent publishes for others to find,
/// and the resolution of a card into a callable <see cref="AIAgent"/>.
/// </summary>
/// <remarks>
/// Because both halves speak A2A, an implementation may serve agents that are not local: a remote
/// card is fetched over the same protocol the local one is published under.
/// </remarks>
public interface IAgentDirectoryService
{
    /// <summary>
    /// Card published for the agent handling <paramref name="intent"/>, or <c>null</c> when no such
    /// agent is known — which callers treat as "nobody answers for this", never as an error.
    /// </summary>
    /// <remarks>
    /// The card names where this instance answers as soon as that is knowable, so a caller serving
    /// it asks per request rather than holding one: nothing ever has to declare the application's
    /// own URL; no moment has to be chosen at which every card learns it at once.
    /// </remarks>
    Task<AgentCard?> GetAgentCardAsync(string intent);

    /// <summary>
    /// The card already projected for <paramref name="intent"/>, or <c>null</c> when none has been
    /// asked for yet. It projects nothing itself.
    /// </summary>
    AgentCard? TryGetProjectedCard(string intent);

    /// <summary>
    /// Resolves the agent <paramref name="peer"/> names by fetching its published card, binding a
    /// client to the interface that card advertises and satisfying the credentials it requires.
    /// </summary>
    /// <remarks>
    /// Best-effort by contract: an unreachable or unpublished agent, or one whose stated requirements
    /// this installation cannot meet, returns <c>null</c> rather than throwing — a colleague that
    /// cannot be reached costs that colleague and nothing more. The card comes back with the agent
    /// because it is the one an implementation actually read: for a colleague published elsewhere
    /// there is no local projection to describe it by.
    /// </remarks>
    /// <param name="peer">Colleague to resolve: its intent and the installation publishing it.</param>
    /// <param name="callerIntent">Asking agent, recorded on the credentials the resolved agent presents.</param>
    Task<(AIAgent Agent, AgentCard Card)?> ResolvePeerAgentAsync(Records.PeerReference peer, string callerIntent);
}
