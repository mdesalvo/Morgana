namespace Morgana.AI.Attributes;

/// <summary>
/// Declares a colleague this agent may consult, exposed to its model as a callable function.
/// Apply multiple times for several colleagues.
/// </summary>
/// <remarks>
/// Declared rather than implicit so the topology is validated at startup, and so an agent pays in
/// prompt tokens only for the colleagues it needs. Read like <see cref="UsesMCPServerAttribute"/>.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public class ConsultsAgentAttribute : Attribute
{
    /// <summary>
    /// Intent handled by the colleague, matching its <see cref="HandlesIntentAttribute"/>.
    /// Validated at startup against the registry of discovered agents.
    /// </summary>
    public string Intent { get; }

    /// <summary>Declares one consultable colleague, whose intent must be handled by a registered agent.</summary>
    /// <exception cref="ArgumentException">Thrown when the intent is null or blank.</exception>
    public ConsultsAgentAttribute(string intent)
    {
        if (string.IsNullOrWhiteSpace(intent))
            throw new ArgumentException("A consulted agent must be declared by a non-empty intent name.", nameof(intent));

        Intent = intent;
    }
}