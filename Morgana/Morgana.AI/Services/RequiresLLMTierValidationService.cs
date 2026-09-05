using System.Reflection;
using Morgana.AI.Attributes;
using Morgana.AI.Interfaces;

namespace Morgana.AI.Services;

/// <summary>
/// Default implementation of <see cref="ILLMTierValidationService"/>: validates every
/// discovered agent's <c>[RequiresLLMTier]</c> declaration via reflection against the tiers
/// actually configured for the active <see cref="ILLMService"/> provider.
/// </summary>
public class RequiresLLMTierValidationService : ILLMTierValidationService
{
    /// <summary>
    /// The active provider, queried only for the set of tiers it actually has configured — the
    /// reference each agent's declared tier is checked against. No completion is ever issued here.
    /// </summary>
    private readonly ILLMService llmService;

    /// <summary>
    /// Initializes a new instance of RequiresLLMTierValidationService.
    /// </summary>
    /// <param name="llmService">LLM service exposing the active provider's configured tiers and per-tier pricing.</param>
    public RequiresLLMTierValidationService(ILLMService llmService)
    {
        this.llmService = llmService;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Startup-fatal by design: an agent silently running on the wrong model is a worse
    /// failure mode than refusing to start — see <see cref="RequiresLLMTierAttribute"/> remarks.
    /// </remarks>
    public void ValidateAgentTiers(IReadOnlyDictionary<string, Type> agentRegistry)
    {
        // The dies this deployment actually holds a model for. A single-model deployment declares one,
        // which is what makes an agent asking for the other one fail here rather than at its first turn.
        IReadOnlyCollection<Records.LLMTier> configuredTiers = llmService.ConfiguredTiers;

        // Two lists because the two failures are fixed in different places: one in the agent's own code,
        // the other in the provider's configuration. Both are collected before either is reported.
        List<string> missingAttribute = [];
        List<string> unconfiguredTier = [];

        foreach ((string intent, Type agentType) in agentRegistry)
        {
            // The die this agent runs on, fixed for its life. A domain author declares it because only
            // they know whether the desk needs deep reasoning or merely routine work.
            RequiresLLMTierAttribute? tierAttribute =
                agentType.GetCustomAttribute<RequiresLLMTierAttribute>();

            // Undeclared, so nobody chose. Defaulting would put an agent on a model its author never
            // weighed, which is the silent wrong-model failure this whole check exists to prevent.
            if (tierAttribute is null)
            {
                missingAttribute.Add($"{agentType.Name} (intent '{intent}')");
                continue;
            }

            // Declared but unavailable here: the choice was made, this deployment cannot honour it. No
            // fallback to the other die, since running a desk on a model nobody chose is the same defect.
            if (!configuredTiers.Contains(tierAttribute.Tier))
                unconfiguredTier.Add($"{agentType.Name} requires tier '{tierAttribute.Tier}' (intent '{intent}')");
        }

        // Reported first because it is the more fundamental of the two: an agent that declares nothing
        // cannot even be weighed against what the provider offers.
        if (missingAttribute.Count > 0)
            throw new InvalidOperationException(
                $"The following Morgana agents are missing the mandatory [RequiresLLMTier] attribute: {string.Join(", ", missingAttribute)}");

        // Named together with what to add, since the fix is one configuration entry rather than a code change.
        if (unconfiguredTier.Count > 0)
            throw new InvalidOperationException(
                $"The following Morgana agents require an LLM tier that is not configured for the active provider " +
                $"(add a Tiers entry under Morgana:LLM:{{Provider}} keyed by that Tier): {string.Join(", ", unconfiguredTier)}");
    }
}