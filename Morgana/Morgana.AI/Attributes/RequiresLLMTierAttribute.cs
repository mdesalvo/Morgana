namespace Morgana.AI.Attributes;

/// <summary>
/// Mandatory on every MorganaAgent: declares the fixed LLMTier (Economy, Efficiency or Performance) it runs on, resolved once
/// at agent creation. Every tier is always configured, so the registry checks only that the attribute is present: an agent
/// without one fails startup rather than run on a model nobody chose.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class RequiresLLMTierAttribute : Attribute
{
    /// <summary>
    /// Gets the LLM tier this agent requires.
    /// </summary>
    public Records.LLMTier Tier { get; }

    /// <summary>
    /// Initializes a new instance of the RequiresLLMTierAttribute.
    /// </summary>
    /// <param name="tier">Power/cost tier this agent must run on.</param>
    public RequiresLLMTierAttribute(Records.LLMTier tier)
    {
        Tier = tier;
    }
}