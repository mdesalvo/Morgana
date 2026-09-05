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
    /// <summary>
    /// The loaded <c>agents.json</c>, deferred behind a <see cref="Lazy{T}"/>: the scan over every
    /// loaded assembly runs once, on first use, rather than at DI construction — which in some
    /// hosting orders happens before the plugin assemblies have finished loading.
    /// </summary>
    private readonly Lazy<AgentConfiguration> agentConfiguration;

    /// <summary>
    /// Logger for the assembly scan: which resource was found, or that none was and Morgana is
    /// therefore running agentless — a legal configuration whose only signal is this warning.
    /// </summary>
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

        // Every assembly in the process, not Morgana's own: a domain lives in a plugin DLL, which
        // PluginLoaderService has already loaded by the time this runs.
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies()
                                                             // A runtime-generated assembly carries no
                                                             // embedded resource to find.
                                                             .Where(a => !a.IsDynamic))
        {
            // Matched by file name alone, as morgana.json is: what a plugin author calls their root
            // namespace is their business.
            string? resourceName = assembly.GetManifestResourceNames()
                                           .FirstOrDefault(n => n.EndsWith(".agents.json", StringComparison.OrdinalIgnoreCase));

            // The assembly that carries the domain. Every other one in the process is passed over in
            // silence, since not carrying a domain is the normal condition of an assembly.
            if (resourceName != null)
            {
                logger.LogInformation("✅ Found agents.json in assembly: {Name}", assembly.GetName().Name);

                try
                {
                    // Named in the manifest yet unreadable: the search moves on rather than settling
                    // for an assembly that advertised a domain it cannot hand over.
                    using Stream? stream = assembly.GetManifestResourceStream(resourceName);
                    if (stream == null)
                    {
                        logger.LogWarning("Could not open stream for {ResourceName}", resourceName);
                        continue;
                    }

                    // The whole domain: the intents the classifier routes on, plus the prompt of every
                    // agent that handles one.
                    AgentConfiguration? config = JsonSerializer.Deserialize<AgentConfiguration>(
                        stream, Records.DefaultJsonSerializerOptions);

                    if (config != null)
                    {
                        logger.LogInformation(
                            "✅ Loaded {IntentsCount} intents and {AgentsCount} agent prompts from agents.json", config.Intents.Count, config.Agents.Count);

                        // The intent list spelled out at startup. It is what the classifier will be given,
                        // the only place an operator reads it back before a conversation exists.
                        foreach (Records.IntentDefinition intent in config.Intents)
                            logger.LogInformation("   📋 Intent: {IntentName} - {IntentDescription}", intent.Name, intent.Description);

                        // One domain per installation, so the first is the only one. Merging several
                        // agents.json files would begin exactly here.
                        return config;
                    }
                }
                catch (Exception ex)
                {
                    // A half-built plugin left in plugins/ costs itself: the search goes on through the
                    // other assemblies rather than taking Morgana's startup down with it.
                    logger.LogError(ex, "Failed to deserialize agents.json from {Name}", assembly.GetName().Name);
                }
            }
        }

        // No domain anywhere in the process. That is agentless mode, which Morgana supports: the
        // registry tolerates an empty intent set, so this warns instead of throwing.
        logger.LogWarning(
            "⚠️  No agents.json found in any loaded assembly. " +
            "Classifier and presentation will have no intents available. " +
            "Add agents.json as embedded resource to your domain project.");

        // An empty domain rather than null: every caller reads intents and prompts without a guard.
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