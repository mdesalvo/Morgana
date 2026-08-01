using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Morgana.AI.Interfaces;

namespace Morgana.AI.Services;

/// <summary>
/// Loads agent configuration from agents.json embedded resource in any loaded assembly.
/// Enables plugin-based domain configuration; graceful fallback (empty config) if none found.
/// Scans all AppDomain assemblies (except dynamic); returns on first successful load.
/// </summary>
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
    /// Scans assemblies for agents.json embedded resource; returns on first successful load.
    /// Logs configuration found (intents + agent prompts); returns empty on not found.
    /// Deserialization errors logged per-assembly; searching continues to next assembly.
    /// </summary>
    /// <returns>AgentConfiguration (non-empty if found, empty if not)</returns>
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
                logger.LogInformation("✅ Found agents.json in assembly: {Name}", assembly.GetName().Name);

                try
                {
                    using Stream? stream = assembly.GetManifestResourceStream(resourceName);
                    if (stream == null)
                    {
                        logger.LogWarning("Could not open stream for {ResourceName}", resourceName);
                        continue;
                    }

                    AgentConfiguration? config = JsonSerializer.Deserialize<AgentConfiguration>(
                        stream, Records.DefaultJsonSerializerOptions);

                    if (config != null)
                    {
                        logger.LogInformation(
                            "✅ Loaded {IntentsCount} intents and {AgentsCount} agent prompts from agents.json", config.Intents.Count, config.Agents.Count);

                        // Log loaded intents for debugging
                        foreach (Records.IntentDefinition intent in config.Intents)
                        {
                            logger.LogInformation("   📋 Intent: {IntentName} - {IntentDescription}", intent.Name, intent.Description);
                        }

                        return config;
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to deserialize agents.json from {Name}", assembly.GetName().Name);
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