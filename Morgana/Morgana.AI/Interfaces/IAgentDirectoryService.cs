using A2A;
using Microsoft.Agents.AI;

namespace Morgana.AI.Interfaces;

/// <summary>Both halves of A2A discovery: the card an agent publishes and the resolution of a colleague's card into a callable agent.</summary>
// Both halves speak A2A, so a colleague published elsewhere is fetched over the same protocol the local one is published under.
public interface IAgentDirectoryService
{
    /// <summary>
    /// The card published for the agent of <paramref name="intent"/>; null when no such agent is known, which callers read as nobody answering for it.
    /// The card names where this installation answers as soon as that is knowable, so its server asks per request instead of
    /// holding one: the application's own URL is never declared and no moment is chosen at which every card learns it at once.
    /// </summary>
    Task<AgentCard?> GetAgentCardAsync(string intent);

    /// <summary>The card already projected for <paramref name="intent"/>; null while none was asked for, since this projects nothing itself.</summary>
    AgentCard? TryGetProjectedCard(string intent);

    /// <summary>
    /// The colleague that <paramref name="peer"/> names, bound to the interface its card advertises with the credentials it requires, together with that card.
    /// Best effort: a colleague that is unreachable, unpublished or asking what this installation cannot meet yields null
    /// rather than an exception, so a colleague that cannot be reached costs that colleague and nothing more. The card comes back
    /// because it is the one actually read: a colleague published elsewhere has no local projection to be described by.
    /// </summary>
    /// <param name="peer">The colleague to resolve: its intent and the installation publishing it.</param>
    /// <param name="callerIntent">The asking agent, recorded on the credentials that the resolved agent presents.</param>
    Task<(AIAgent Agent, AgentCard Card)?> ResolvePeerAgentAsync(Records.PeerReference peer, string callerIntent);
}