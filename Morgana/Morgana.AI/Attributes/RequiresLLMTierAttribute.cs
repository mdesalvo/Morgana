namespace Morgana.AI.Attributes;

/// <summary>Declares the tier that an agent runs on, mandatory on every <see cref="Abstractions.MorganaAgent"/>.</summary>
// Fixed by the declaration and read once when the agent is built. An agent without it stops the startup,
// because a silent fallback would run it on a model nobody chose.
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class RequiresLLMTierAttribute : Attribute
{
    /// <summary>The tier that the agent runs on.</summary>
    public Records.LLMTier Tier { get; }

    /// <summary>Declares the tier that the agent runs on.</summary>
    /// <param name="tier">The tier whose model the agent's every call goes to.</param>
    public RequiresLLMTierAttribute(Records.LLMTier tier)
    {
        // The agent adapter reads it to pick the tier's chat client.
        Tier = tier;
    }
}