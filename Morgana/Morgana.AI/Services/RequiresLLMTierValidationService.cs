using System.Reflection;
using Microsoft.Extensions.Logging;
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
    private readonly ILogger logger;

    /// <summary>
    /// Initializes a new instance of RequiresLLMTierValidationService.
    /// </summary>
    /// <param name="llmService">LLM service exposing the active provider's configured tiers and per-tier pricing.</param>
    /// <param name="logger">Logger used to flag non-fatal but suspicious tier declarations (see <see cref="ValidateAgentTiers"/>).</param>
    public RequiresLLMTierValidationService(ILLMService llmService, ILogger logger)
    {
        this.llmService = llmService;
        this.logger = logger;
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

        // On an Omni deployment there is only one model, and it answers for every tier — so no
        // agent can ever be missing a tier here, whatever it declared.
        bool isOmniDeployment = configuredTiers.Contains(Records.LLMTier.Omni);

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

            // Omni is a deployment-level escape hatch ("this provider has one model, it
            // serves every tier"), not an authoring choice — see RequiresLLMTierAttribute
            // remarks. Declaring it on an agent works (Omni always wins where configured)
            // but is very likely a copy-paste mistake: flag it so it doesn't go unnoticed,
            // without failing startup since it is not actually broken.
            if (tierAttribute.Tier == Records.LLMTier.Omni)
                logger.LogWarning(
                    "{AgentTypeName} (intent '{Intent}') declares [RequiresLLMTier(LLMTier.Omni)]. Omni is meant " +
                    "as a deployment-level override, not an agent authoring choice — this only works while the " +
                    "active provider is Omni-only, and silently stops working the moment the deployment adds " +
                    "explicit Low/Moderate/High models. Consider declaring the tier the agent actually needs.",
                    agentType.Name, intent);

            if (!isOmniDeployment && !configuredTiers.Contains(tierAttribute.Tier))
                unconfiguredTier.Add($"{agentType.Name} requires tier '{tierAttribute.Tier}' (intent '{intent}')");
        }

        if (missingAttribute.Count > 0)
            throw new InvalidOperationException(
                $"The following Morgana agents are missing the mandatory [RequiresLLMTier] attribute: {string.Join(", ", missingAttribute)}");

        if (unconfiguredTier.Count > 0)
            throw new InvalidOperationException(
                $"The following Morgana agents require an LLM tier that is not configured for the active provider " +
                $"(add a Models entry under Morgana:LLM:{{Provider}} keyed by that Tier, or a single \"Omni\" entry " +
                $"to serve every tier): {string.Join(", ", unconfiguredTier)}");
    }
}