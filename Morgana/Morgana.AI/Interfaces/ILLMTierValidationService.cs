namespace Morgana.AI.Interfaces;

/// <summary>
/// Validates <c>[RequiresLLMTier]</c> declarations across discovered Morgana agents against the
/// tiers actually configured for the active <see cref="ILLMService"/> provider. A dedicated
/// extension point — kept separate from <see cref="IAgentRegistryService"/> (whose concern is
/// intent↔agent discovery, not LLM cost/tier policy) so a deployment can swap in a different
/// tier-governance policy without touching intent routing at all.
/// </summary>
/// <remarks>
/// Startup-fatal by design in the default implementation
/// (<c>Services.RequiresLLMTierValidationService</c>): an agent silently running on a tier the
/// active provider never configured is a worse failure mode than refusing to start — see
/// <see cref="Attributes.RequiresLLMTierAttribute"/> remarks.
/// </remarks>
public interface ILLMTierValidationService
{
    /// <summary>
    /// Validates every entry in <paramref name="agentRegistry"/>.
    /// </summary>
    /// <param name="agentRegistry">Intent name → agent type map, as built by the active <see cref="IAgentRegistryService"/> implementation.</param>
    /// <exception cref="InvalidOperationException">Thrown on any validation failure (see implementation remarks for the specific cases).</exception>
    void ValidateAgentTiers(IReadOnlyDictionary<string, Type> agentRegistry);
}