namespace Morgana.AI.Interfaces;

/// <summary>Maps each intent to the agent type whose <see cref="Attributes.HandlesIntentAttribute"/> claims it.</summary>
// Agents are discovered from the loaded plugins, so no intent is wired by hand.
public interface IAgentRegistryService
{
    /// <summary>
    /// The agent type that handles <paramref name="intent"/>; null when no agent does.
    /// On null the router answers the request itself with Morgana's presentation.
    /// </summary>
    Type? ResolveAgentFromIntent(string intent);

    /// <summary>Every intent that a discovered agent handles.</summary>
    IEnumerable<string> GetAllIntents();
}