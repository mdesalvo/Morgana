namespace Morgana.AI.Attributes;

/// <summary>
/// Mandatory on every MorganaAgent: declares fixed LLMTier (Efficiency/Performance). Static declaration, resolved once
/// at agent creation, never changes. Startup validation by RequiresLLMTierValidationService ensures declared tier is
/// configured in appsettings.json. Missing tier fails application startup (prevent silent fallback).
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