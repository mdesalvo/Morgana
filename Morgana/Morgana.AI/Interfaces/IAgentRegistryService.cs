namespace Morgana.AI.Interfaces;

/// <summary>
/// Maps each intent to the agent type that handles it, so that plugin agents are routable without the host naming them.
/// </summary>
public interface IAgentRegistryService
{
    /// <summary>
    /// Returns the agent type that handles <paramref name="intent"/>, or <c>null</c> when none does.
    /// Callers treat <c>null</c> as an unrecognized intent to report to the user, never as a fault.
    /// </summary>
    Type? ResolveAgentFromIntent(string intent);

    /// <summary>
    /// Returns every intent that has a handling agent, so that the configured intents can be compared with the agents that exist.
    /// </summary>
    IEnumerable<string> GetAllIntents();
}