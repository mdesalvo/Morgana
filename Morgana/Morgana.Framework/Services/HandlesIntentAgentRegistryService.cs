using System.Reflection;
using Morgana.Framework.Abstractions;
using Morgana.Framework.Attributes;
using Morgana.Framework.Interfaces;

namespace Morgana.Framework.Services;

/// <summary>
/// Implementation of IAgentRegistryService that discovers agents via [HandlesIntent] attribute scanning.
/// Provides runtime agent discovery with bidirectional validation against configuration.
/// </summary>
/// <remarks>
/// <para><strong>Purpose:</strong></para>
/// <para>This service builds a registry of available agents by scanning all loaded assemblies for
/// MorganaAgent-derived classes decorated with [HandlesIntent] attribute. It performs comprehensive
/// validation to ensure configuration consistency between agents.json and agent implementations.</para>
/// <para><strong>Discovery and Validation Flow:</strong></para>
/// <code>
/// 1. Application Startup
/// 2. PluginLoaderService loads domain assemblies
/// 3. HandlesIntentAgentRegistryService initializes
/// 4. Scans all assemblies for MorganaAgent classes
/// 5. Extracts intent from [HandlesIntent] attribute
/// 6. Builds intent → Type registry
/// 7. Loads intents from agents.json
/// 8. Validates bidirectional consistency:
///    - All configured intents have agent implementations
///    - All agent implementations have configuration entries
/// 9. Throws exception if validation fails
/// 10. Registry ready for RouterActor use
/// </code>
/// <para><strong>Bidirectional Validation:</strong></para>
/// <para>The service performs two critical validation checks:</para>
/// <list type="bullet">
/// <item><term>Configuration → Implementation</term><description>
/// Every intent in agents.json (except "other") must have a corresponding [HandlesIntent] agent.
/// Prevents ClassifierActor from routing to non-existent agents.
/// </description></item>
/// <item><term>Implementation → Configuration</term><description>
/// Every [HandlesIntent] agent must have a corresponding intent definition in agents.json.
/// Prevents orphaned agent implementations that can never be reached.
/// </description></item>
/// </list>
/// <para><strong>Validation Failure Examples:</strong></para>
/// <code>
/// // Case 1: Intent configured but no agent
/// agents.json: { "Name": "billing", ... }
/// Code: No class with [HandlesIntent("billing")]
/// Error: "There are intents not handled by any Morgana agent: billing"
///
/// // Case 2: Agent implemented but not configured
/// Code: [HandlesIntent("billing")] public class BillingAgent : MorganaAgent { }
/// agents.json: No "billing" intent defined
/// Error: "There are Morgana agents handling an undeclared intent: billing"
/// </code>
/// <para><strong>RouterActor Integration:</strong></para>
/// <code>
/// // RouterActor constructor
/// foreach (string intent in agentRegistryService.GetAllIntents())
/// {
///     Type? agentType = agentRegistryService.ResolveAgentFromIntent(intent);
///     agents[intent] = await Context.System.GetOrCreateAgent(agentType, intent, conversationId);
/// }
/// // Result: All configured intents have instantiated agents ready for routing
/// </code>
/// </remarks>
public class HandlesIntentAgentRegistryService : IAgentRegistryService
{
    private readonly IAgentConfigurationService agentConfigService;

    /// <summary>
    /// Registry mapping intent names to agent types.
    /// Built during service initialization via assembly scanning.
    /// Case-insensitive string comparison for intent matching.
    /// </summary>
    private readonly Lazy<Dictionary<string, Type>> intentToAgentType;

    /// <summary>
    /// Initializes a new instance of HandlesIntentAgentRegistryService.
    /// Performs agent discovery and bidirectional validation immediately.
    /// </summary>
    /// <param name="agentConfigService">Service for loading intent configuration from agents.json</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown if bidirectional validation fails (missing agents or missing configuration)
    /// </exception>
    public HandlesIntentAgentRegistryService(IAgentConfigurationService agentConfigService)
    {
        this.agentConfigService = agentConfigService;

        intentToAgentType = new Lazy<Dictionary<string, Type>>(InitializeRegistry);
    }

    /// <summary>
    /// Initializes the agent registry by scanning assemblies and validating against configuration.
    /// Performs bidirectional validation to ensure consistency.
    /// </summary>
    /// <returns>Dictionary mapping intent names to agent types</returns>
    /// <exception cref="InvalidOperationException">Thrown on validation failure</exception>
    /// <remarks>
    /// <para><strong>Discovery Process:</strong></para>
    /// <list type="number">
    /// <item>Get all assemblies from AppDomain (excluding dynamic assemblies)</item>
    /// <item>Get all types from each assembly (handle ReflectionTypeLoadException)</item>
    /// <item>Filter for concrete MorganaAgent-derived classes</item>
    /// <item>Extract [HandlesIntent] attribute from each agent</item>
    /// <item>Build intent → Type dictionary (case-insensitive)</item>
    /// </list>
    /// <para><strong>Validation Process:</strong></para>
    /// <list type="number">
    /// <item>Load all intents from agents.json via IAgentConfigurationService</item>
    /// <item>Extract intent names (excluding "other" intent)</item>
    /// <item>Compare configured intents with discovered agents</item>
    /// <item>Check for unimplemented intents (in config but no agent)</item>
    /// <item>Check for unconfigured agents (has agent but not in config)</item>
    /// <item>Throw exception with detailed error message if mismatches found</item>
    /// </list>
    /// <para><strong>Error Messages:</strong></para>
    /// <code>
    /// // Missing agent implementations
    /// "There are intents not handled by any Morgana agent: billing, contract"
    ///
    /// // Missing configuration entries
    /// "There are Morgana agents handling an undeclared intent: billing, contract"
    /// </code>
    /// <para><strong>Exception Handling:</strong></para>
    /// <para>ReflectionTypeLoadException is caught during type scanning to handle partially-loaded
    /// assemblies gracefully. This prevents the entire discovery process from failing if one assembly
    /// has loading issues.</para>
    /// </remarks>
    private Dictionary<string, Type> InitializeRegistry()
    {
        Dictionary<string, Type> registry = new(StringComparer.OrdinalIgnoreCase);

        // Discovery of available agents with their declared intent
        // Scan ALL loaded assemblies, not just executing assembly
        IEnumerable<Type> morganaAgentTypes = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic)
            .SelectMany(a =>
            {
                try
                {
                    return a.GetTypes();
                }
                catch (ReflectionTypeLoadException)
                {
                    // Gracefully handle assemblies that fail to load completely
                    return [];
                }
            })
            .Where(t => t is { IsClass: true, IsAbstract: false } && t.IsSubclassOf(typeof(MorganaAgent)));

        foreach (Type? morganaAgentType in morganaAgentTypes)
        {
            HandlesIntentAttribute? handlesIntentAttribute = morganaAgentType.GetCustomAttribute<HandlesIntentAttribute>();
            if (handlesIntentAttribute != null)
                registry[handlesIntentAttribute.Intent] = morganaAgentType;
        }

        #region Validation
        // Bidirectional validation of Morgana agents and intents
        // Load intents from domain-specific configuration
        List<Records.IntentDefinition> allIntents = agentConfigService.GetIntentsAsync().GetAwaiter().GetResult();

        // Extract intent names, excluding "other" (special fallback intent with no dedicated agent)
        HashSet<string> classifierIntents = allIntents
            .Where(intent => !string.Equals(intent.Name, "other", StringComparison.OrdinalIgnoreCase))
            .Select(intent => intent.Name)
            .ToHashSet();

        HashSet<string> registeredIntents = [.. registry.Keys];

        // Check 1: Configured intents without agent implementations
        List<string> unregisteredClassifierIntents = [.. classifierIntents.Except(registeredIntents)];
        if (unregisteredClassifierIntents.Count > 0)
            throw new InvalidOperationException(
                $"There are intents not handled by any Morgana agent: {string.Join(", ", unregisteredClassifierIntents)}");

        // Check 2: Agent implementations without configuration entries
        List<string> unconfiguredAgentIntents = [.. registeredIntents.Except(classifierIntents)];
        if (unconfiguredAgentIntents.Count > 0)
            throw new InvalidOperationException(
                $"There are Morgana agents handling an undeclared intent: {string.Join(", ", unconfiguredAgentIntents)}");
        #endregion

        return registry;
    }

    /// <summary>
    /// Resolves the agent type that handles a specific intent.
    /// </summary>
    /// <param name="intent">Intent name to resolve (e.g., "billing")</param>
    /// <returns>Type of agent class handling this intent, or null if not found</returns>
    /// <remarks>
    /// <para><strong>Case-Insensitive Matching:</strong></para>
    /// <para>Intent matching uses StringComparer.OrdinalIgnoreCase, so "billing", "Billing",
    /// and "BILLING" all resolve to the same agent type.</para>
    /// <para><strong>Null Return:</strong></para>
    /// <para>Returns null for unrecognized intents rather than throwing. This allows
    /// RouterActor to provide user-friendly error messages for unsupported intents.</para>
    /// </remarks>
    public Type? ResolveAgentFromIntent(string intent)
        => intentToAgentType.Value.GetValueOrDefault(intent);

    /// <summary>
    /// Gets all registered intent names from discovered agents.
    /// </summary>
    /// <returns>Enumerable of intent names that have agent implementations</returns>
    /// <remarks>
    /// <para><strong>Usage:</strong></para>
    /// <para>RouterActor uses this to pre-create all agents during initialization,
    /// ensuring agents are ready before the first request arrives.</para>
    /// </remarks>
    public IEnumerable<string> GetAllIntents()
        => intentToAgentType.Value.Keys;
}