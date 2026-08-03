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

        // Lazy, not loaded eagerly here: reflection over every loaded assembly (see
        // LoadAgentConfiguration below) is comparatively expensive and only needs to run once, the
        // first time an intent or a prompt is actually asked for — not on DI construction, which
        // may happen before plugin assemblies have even finished loading in some hosting orders.
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

        // Every assembly currently loaded into the process is a candidate — not just Morgana's
        // own — because a domain author's plugin DLL (loaded by PluginLoaderService before this
        // service ever runs, see Program.cs) is exactly where agents.json is expected to live.
        // IsDynamic assemblies are skipped because they never carry embedded resources (they're
        // runtime-generated, e.g. by reflection emit) and GetManifestResourceNames would just
        // throw NotSupportedException on them.
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

                        // First successful load wins, full stop — this method does NOT keep
                        // scanning to merge a second agents.json from a second plugin assembly.
                        // Today's deployment model is one domain plugin per Morgana instance, so
                        // this is a deliberate simplification, not an oversight: if a future
                        // multi-plugin scenario needs several agents.json files merged together,
                        // this early return is exactly the line that would need to change.
                        return config;
                    }
                }
                catch (Exception ex)
                {
                    // Deliberately caught per-assembly rather than left to propagate: a malformed
                    // agents.json in ONE assembly (e.g. a half-built plugin sitting in the plugins/
                    // folder) should not prevent scanning the rest for a valid one elsewhere, and
                    // should definitely not crash Morgana's startup over a single bad resource file.
                    logger.LogError(ex, "Failed to deserialize agents.json from {Name}", assembly.GetName().Name);
                }
            }
        }

        // Reached only if no assembly contributed a usable agents.json at all. This is a
        // supported, documented mode ("agentless") rather than a startup failure — see
        // EmbeddedAgentConfigurationService's class summary and HandlesIntentAgentRegistryService,
        // which both tolerate an empty intent/agent set gracefully.
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