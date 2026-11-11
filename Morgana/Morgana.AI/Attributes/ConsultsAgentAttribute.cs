namespace Morgana.AI.Attributes;

/// <summary>
/// Declares a colleague that this agent may consult, offered to its model as the function <c>consult_{intent}</c>.
/// Declared rather than implicit, so the topology is validated at startup and an agent pays in prompt tokens
/// only for the colleagues it needs.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public class ConsultsAgentAttribute : Attribute
{
    /// <summary>The intent that the colleague handles, as its <see cref="HandlesIntentAttribute"/> names it.</summary>
    public string Intent { get; }

    /// <summary>
    /// The partner publishing the colleague, as <c>Morgana:AgentToAgent:Partners</c> names it; null for an agent of this installation.
    /// A name and not an address: whose agent to call is the agent author's decision, where that agent runs is the deployment's.
    /// </summary>
    public string? Instance { get; }

    /// <summary>Declares one colleague, of this installation or of a partner.</summary>
    /// <param name="intent">The intent the colleague handles; a colleague of this installation must be handled by a registered agent.</param>
    /// <param name="instance">The partner publishing the colleague, omitted for a colleague of this installation.</param>
    /// <exception cref="ArgumentException">The intent is null or blank.</exception>
    public ConsultsAgentAttribute(string intent, string? instance = null)
    {
        // A colleague without an intent names no agent at all, so the declaration fails where it is written.
        if (string.IsNullOrWhiteSpace(intent))
            throw new ArgumentException("A consulted agent must be declared by a non-empty intent name.", nameof(intent));

        // The intent is checked against the registry of discovered agents at startup.
        Intent = intent;

        // The name is matched against a configuration entry and a stray space would name a partner nobody declared,
        // an error whose cause is invisible on screen.
        Instance = string.IsNullOrWhiteSpace(instance) ? null : instance.Trim();
    }
}