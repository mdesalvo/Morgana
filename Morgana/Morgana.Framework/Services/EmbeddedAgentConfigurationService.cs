using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Morgana.Framework.Interfaces;

namespace Morgana.Framework.Services;

/// <summary>
/// Implementation of IAgentConfigurationService that loads agent configuration from agents.json embedded resource.
/// Scans all loaded assemblies to find agents.json and provides graceful fallback if not found.
/// </summary>
/// <remarks>
/// <para><strong>Purpose:</strong></para>
/// <para>This service enables plugin-based domain configuration by scanning all loaded assemblies
/// (including dynamically loaded plugins) for an embedded agents.json resource. This allows
/// domain-specific configuration to live alongside domain code in plugin assemblies.</para>
/// <para><strong>Discovery Strategy:</strong></para>
/// <list type="number">
/// <item>Enumerate all loaded assemblies in AppDomain</item>
/// <item>Skip dynamic assemblies (cannot contain embedded resources)</item>
/// <item>Search manifest resources for any ending with ".agents.json"</item>
/// <item>Load and deserialize first agents.json found</item>
/// <item>Return graceful fallback (empty config) if none found</item>
/// </list>
/// <para><strong>Typical Deployment:</strong></para>
/// <code>
/// Solution Structure:
/// ├── Morgana.Actors/
/// │   └── (actors code, no agents.json)
/// ├── Morgana.Agents/
/// │   └── morgana.json (framework prompts)
/// └── Morgana.Example/
///     ├── agents.json (domain configuration)
///     ├── BillingAgent.cs
///     ├── ContractAgent.cs
///     └── TroubleshootingAgent.cs
///
/// At runtime:
/// 1. PluginLoaderService loads Morgana.Example.dll
/// 2. EmbeddedAgentConfigurationService scans all assemblies
/// 3. Finds agents.json in Morgana.Example.dll
/// 4. Loads intents and agent prompts from it
/// </code>
/// <para><strong>Graceful Degradation:</strong></para>
/// <para>If no agents.json is found, the service returns empty configuration rather than throwing.
/// This allows the framework to operate in presentation-only mode (no domain agents, only "other" intent).</para>
/// <para><strong>Alternative Implementations:</strong></para>
/// <para>This embedded resource approach is the default. Alternative implementations could load from:</para>
/// <list type="bullet">
/// <item>File system (appsettings.json or standalone agents.json file)</item>
/// <item>Database (SQL, MongoDB, etc.)</item>
/// <item>External API or configuration service</item>
/// <item>Azure Blob Storage or AWS S3</item>
/// </list>
/// </remarks>
public class EmbeddedAgentConfigurationService : IAgentConfigurationService
{
    private readonly Lazy<AgentConfiguration> agentConfiguration;
    private readonly ILogger logger;

    /// <summary>
    /// Initializes a new instance of EmbeddedAgentConfigurationService.
    /// Immediately loads agent configuration from embedded agents.json resource.
    /// </summary>
    /// <param name="logger">Logger instance for configuration loading diagnostics</param>
    public EmbeddedAgentConfigurationService(ILogger logger)
    {
        this.logger = logger;

        agentConfiguration = new Lazy<AgentConfiguration>(LoadAgentConfiguration);
    }

    /// <summary>
    /// Gets intent definitions from the loaded agents.json configuration.
    /// </summary>
    /// <returns>List of intent definitions (empty if no agents.json found)</returns>
    public Task<List<Records.IntentDefinition>> GetIntentsAsync()
    {
        return Task.FromResult(agentConfiguration.Value.Intents);
    }

    /// <summary>
    /// Gets agent prompt configurations from the loaded agents.json configuration.
    /// </summary>
    /// <returns>List of agent prompts (empty if no agents.json found)</returns>
    public Task<List<Records.Prompt>> GetAgentPromptsAsync()
    {
        return Task.FromResult(agentConfiguration.Value.Agents);
    }

    /// <summary>
    /// Loads agent configuration by scanning all loaded assemblies for agents.json embedded resource.
    /// Returns empty configuration if no agents.json found (graceful degradation).
    /// </summary>
    /// <returns>
    /// AgentConfiguration with loaded intents and prompts, or empty configuration if not found.
    /// </returns>
    /// <remarks>
    /// <para><strong>Assembly Scanning Process:</strong></para>
    /// <list type="number">
    /// <item>Get all assemblies from AppDomain.CurrentDomain</item>
    /// <item>Filter out dynamic assemblies (cannot have embedded resources)</item>
    /// <item>For each assembly, get manifest resource names</item>
    /// <item>Find first resource ending with ".agents.json" (case-insensitive)</item>
    /// <item>Open resource stream and deserialize JSON</item>
    /// <item>Validate and log loaded configuration</item>
    /// </list>
    /// <para><strong>Logging Output Examples:</strong></para>
    /// <code>
    /// // Success case
    /// Searching for agents.json in loaded assemblies...
    /// ✅ Found agents.json in assembly: Morgana.Example
    /// ✅ Loaded 3 intents and 3 agent prompts from agents.json
    ///    📋 Intent: billing - requests to view invoices...
    ///    📋 Intent: contract - requests to summarize contract...
    ///    📋 Intent: troubleshooting - requests to solve technical problems...
    ///
    /// // No configuration found
    /// Searching for agents.json in loaded assemblies...
    /// ⚠️  No agents.json found in any loaded assembly.
    /// Classifier and presentation will have no intents available.
    /// Add agents.json as embedded resource to your domain project.
    /// </code>
    /// <para><strong>Error Handling:</strong></para>
    /// <list type="bullet">
    /// <item><term>Resource not found</term><description>Returns empty config with warning log</description></item>
    /// <item><term>Deserialization error</term><description>Logs error, continues searching other assemblies</description></item>
    /// <item><term>Stream open failure</term><description>Logs warning, continues searching</description></item>
    /// </list>
    /// <para><strong>First-Match Strategy:</strong></para>
    /// <para>Returns immediately after finding and successfully loading the first agents.json.
    /// If multiple assemblies contain agents.json, only the first one found is used.</para>
    /// </remarks>
    private AgentConfiguration LoadAgentConfiguration()
    {
        logger.LogInformation("Searching for agents.json in loaded assemblies...");

        // Search for agents.json in ALL loaded assemblies
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic))
        {
            string? resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(".agents.json", StringComparison.OrdinalIgnoreCase));

            if (resourceName != null)
            {
                logger.LogInformation($"✅ Found agents.json in assembly: {assembly.GetName().Name}");

                try
                {
                    using Stream? stream = assembly.GetManifestResourceStream(resourceName);
                    if (stream == null)
                    {
                        logger.LogWarning($"Could not open stream for {resourceName}");
                        continue;
                    }

                    AgentConfiguration? config = JsonSerializer.Deserialize<AgentConfiguration>(
                        stream,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                    if (config != null)
                    {
                        logger.LogInformation(
                            $"✅ Loaded {config.Intents.Count} intents and {config.Agents.Count} agent prompts from agents.json");

                        // Log loaded intents for debugging
                        foreach (Records.IntentDefinition intent in config.Intents)
                        {
                            logger.LogInformation($"   📋 Intent: {intent.Name} - {intent.Description}");
                        }

                        return config;
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, $"Failed to deserialize agents.json from {assembly.GetName().Name}");
                }
            }
        }

        // Fallback: no agents.json found
        logger.LogWarning(
            "⚠️  No agents.json found in any loaded assembly. " +
            "Classifier and presentation will have no intents available. " +
            "Add agents.json as embedded resource to your domain project.");

        return new AgentConfiguration([], []);
    }

    /// <summary>
    /// Internal record for deserializing agents.json structure.
    /// Maps JSON structure to strongly-typed records.
    /// </summary>
    /// <param name="Intents">List of intent definitions for classification and presentation</param>
    /// <param name="Agents">List of agent prompt configurations</param>
    private record AgentConfiguration(
        List<Records.IntentDefinition> Intents,
        List<Records.Prompt> Agents);
}