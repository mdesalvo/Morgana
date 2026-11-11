namespace Morgana.AI.Interfaces;

/// <summary>
/// Maps each intent to the tool class whose <see cref="Attributes.ProvidesToolForIntentAttribute"/> names it.
/// Tool classes are discovered from the loaded plugins once they are loaded, so no tool is wired by hand.
/// </summary>
public interface IToolRegistryService
{
    /// <summary>The tool class that serves the agent of <paramref name="intent"/>; null when the agent has no domain tools.</summary>
    Type? FindToolTypeForIntent(string intent);

    /// <summary>Every registered tool class under the intent it serves, which the agent registry checks against the agents at startup.</summary>
    IReadOnlyDictionary<string, Type> GetAllRegisteredTools();
}