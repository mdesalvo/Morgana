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
    Task<AgentCard?> GetAgentCardAsync(string intent);

    /// <summary>Fills in, on every card already projected, the interface this instance answers on.</summary>
    /// <remarks>
    /// Cards are projected while the endpoints are still being mapped, before the server has bound.
    /// Called once the address exists and before any request can read a card, so that nothing ever
    /// has to declare the application's own URL.
    /// </remarks>
    Task PublishInterfacesAsync();

    /// <summary>
    /// Resolves the agent handling <paramref name="intent"/> by fetching its published card and
    /// binding a client to the interface that card advertises.
    /// </summary>
    /// <remarks>
    /// Best-effort by contract: an unreachable or unpublished agent returns <c>null</c> rather than
    /// throwing, so a colleague that cannot be reached costs that colleague and nothing more.
    /// </remarks>
    /// <param name="callerIntent">Asking agent, recorded on the credentials the resolved agent presents.</param>
    Task<AIAgent?> ResolvePeerAgentAsync(string intent, string callerIntent);
}
