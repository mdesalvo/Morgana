namespace Morgana.AI.Attributes;

/// <summary>
/// Declares the fixed, "existential" <see cref="Records.LLMTier"/> a <c>MorganaAgent</c>
/// subclass runs on. Mandatory on every Morgana agent — a domain expert
/// authoring an agent must make an explicit economic/quality judgment call for it, rather
/// than silently inheriting whatever tier happens to be cheap or expensive.
/// </summary>
/// <remarks>
/// <para><strong>Static declaration, no runtime escalation.</strong> The tier is resolved once
/// at agent creation (<see cref="Adapters.MorganaAgentAdapter.CreateAgent"/>) and never changes
/// for the lifetime of that agent instance.</para>
/// <para><strong>Startup validation:</strong> <c>RequiresLLMTierValidationService</c> verifies,
/// for every discovered agent, that the declared tier is actually configured (a
/// <c>Models</c> entry keyed by matching <see cref="Records.LLMTier"/>) under the active
/// provider's section in <c>appsettings.json</c>. A missing tier fails application startup —
/// an agent silently falling back to a different model than the one its author intended would
/// be a worse failure mode than refusing to start.</para>
/// <para><strong>Usage example:</strong></para>
/// <code>
/// [HandlesIntent("contract")]
/// [RequiresLLMTier(Records.LLMTier.Efficiency)]
/// public class ContractAgent : MorganaAgent { ... }
/// </code>
/// </remarks>
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