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
        // Which tiers the active provider actually has a model for.
        IReadOnlyCollection<Records.LLMTier> configuredTiers = llmService.ConfiguredTiers;

        // Every agent gets checked before anything is thrown, so a misconfigured deployment
        // reports every problem agent at once instead of one at a time across repeated restarts.
        List<string> missingAttribute = [];
        List<string> unconfiguredTier = [];

        foreach ((string intent, Type agentType) in agentRegistry)
        {
            RequiresLLMTierAttribute? tierAttribute =
                agentType.GetCustomAttribute<RequiresLLMTierAttribute>();

            if (tierAttribute is null)
            {
                missingAttribute.Add($"{agentType.Name} (intent '{intent}')");
                continue;
            }

            if (!configuredTiers.Contains(tierAttribute.Tier))
                unconfiguredTier.Add($"{agentType.Name} requires tier '{tierAttribute.Tier}' (intent '{intent}')");
        }

        if (missingAttribute.Count > 0)
            throw new InvalidOperationException(
                $"The following Morgana agents are missing the mandatory [RequiresLLMTier] attribute: {string.Join(", ", missingAttribute)}");

        if (unconfiguredTier.Count > 0)
            throw new InvalidOperationException(
                $"The following Morgana agents require an LLM tier that is not configured for the active provider " +
                $"(add a Tiers entry under Morgana:LLM:{{Provider}} keyed by that Tier): {string.Join(", ", unconfiguredTier)}");
    }
}