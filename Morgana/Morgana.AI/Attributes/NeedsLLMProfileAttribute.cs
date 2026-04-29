namespace Morgana.AI.Attributes;

/// <summary>
/// Declares the minimum LLM capability profile a <see cref="Abstractions.MorganaAgent"/> requires
/// to perform its task. The framework's LLM resolver matches this declaration against the
/// operator-defined <see cref="Records.LLMCatalogEntry"/> set at startup, picking the cheapest
/// entry whose <see cref="Records.LLMProfile"/> satisfies the request (never downgrade,
/// upgrade allowed when no exact match exists).
/// </summary>
/// <remarks>
/// <para><strong>Mandatory on plugin agents.</strong> Startup validation rejects any
/// <see cref="Abstractions.MorganaAgent"/> subclass without this attribute — silent default to
/// <see cref="Records.LLMProfile.Basic"/> is intentionally not provided.</para>
/// <para><strong>Authority separation.</strong> Plugin authors declare only the *minimum capability*
/// their agent needs, not a specific model or provider. Model and provider are operator concerns,
/// expressed through the catalog. This is the inversion that the v0.22 release theme formalises.</para>
/// <para><strong>Usage:</strong></para>
/// <code>
/// [HandlesIntent("billing")]
/// [NeedsLLMProfile(Records.LLMProfile.Medium)]
/// public class BillingAgent : MorganaAgent { ... }
/// </code>
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class NeedsLLMProfileAttribute : Attribute
{
    /// <summary>
    /// Minimum capability tier this agent requires. The resolver may assign a higher tier if
    /// the catalog has no exact match, but never a lower one (fail-closed otherwise).
    /// </summary>
    public Records.LLMProfile Profile { get; }

    /// <summary>
    /// Initializes a new instance of <see cref="NeedsLLMProfileAttribute"/>.
    /// </summary>
    /// <param name="profile">Minimum LLM capability tier required by this agent.</param>
    public NeedsLLMProfileAttribute(Records.LLMProfile profile)
    {
        Profile = profile;
    }
}