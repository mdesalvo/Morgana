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

    /// <summary>
    /// Partner publishing the colleague, as named in <c>Morgana:AgentToAgent:Partners</c>, or
    /// <c>null</c> when it is an agent of this installation. A name and not an address: whose desk to
    /// call is the agent author's decision, where that desk runs is the deployment's.
    /// </summary>
    public string? Partner { get; }

    /// <summary>Declares one consultable colleague, of this installation or of a declared partner.</summary>
    /// <param name="intent">Intent the colleague handles; must be handled by a registered agent when it is local.</param>
    /// <param name="partner">Partner publishing it, omitted for a colleague of this installation.</param>
    /// <exception cref="ArgumentException">Thrown when the intent is null or blank.</exception>
    public ConsultsAgentAttribute(string intent, string? partner = null)
    {
        if (string.IsNullOrWhiteSpace(intent))
            throw new ArgumentException("A consulted agent must be declared by a non-empty intent name.", nameof(intent));

        Intent = intent;
        // Trimmed rather than taken as typed: the name is matched against a configuration entry, and
        // a stray space is a partner nobody declared — an error whose cause is invisible on screen.
        Partner = string.IsNullOrWhiteSpace(partner) ? null : partner.Trim();
    }
}