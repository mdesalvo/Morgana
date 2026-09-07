using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Morgana.AI.Interfaces;

namespace Morgana.AI.Services;

/// <summary>
/// Loads agent configuration from the agents.json embedded resource of every loaded assembly that
/// carries one, merged into the single domain this installation serves. Graceful fallback (empty
/// config) if none is found; a name claimed twice is refused rather than resolved.
/// </summary>
/// <remarks>
/// Several plugins may each bring part of a domain — desks that belong to one organization without
/// belonging to one deliverable. What they may not do is disagree: two plugins declaring the same
/// intent, or two prompts under one id, describe two different desks answering to one name and
/// nothing downstream could tell which was meant.
/// <para>What a domain cannot bring at all is the complement of itself. The catch-all is what a
/// request matching no desk is, which is the classifier's business and is described in the
/// classifier's own prompt: the name is reserved, and a domain declaring it is refused here rather
/// than quietly corrected — the same way a partner is refused the name of this installation's own
/// ring.</para>
/// </remarks>
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
    /// Scans every loaded assembly for an agents.json embedded resource and merges what it finds.
    /// Logs the resulting domain; returns empty when no assembly carries one.
    /// Deserialization errors logged per-assembly; searching continues to next assembly.
    /// </summary>
    /// <returns>The merged domain (empty when nothing was found)</returns>
    /// <exception cref="InvalidOperationException">Two assemblies claim one intent or one prompt id.</exception>
    private AgentConfiguration LoadAgentConfiguration()
    {
        logger.LogInformation("Searching for agents.json in loaded assemblies...");

        // The domain as it is being assembled, with the assembly each name arrived from: a collision
        // is only diagnosable by naming both plugins, which is the whole point of recording it.
        List<Records.IntentDefinition> mergedIntents = [];
        List<Records.Prompt> mergedAgents = [];
        Dictionary<string, string> declaringAssemblyByIntent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> declaringAssemblyByPrompt = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

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
                        string declaringAssembly = assembly.GetName().Name ?? resourceName;

                        logger.LogInformation(
                            "✅ Loaded {IntentsCount} intents and {AgentsCount} agent prompts from agents.json", config.Intents.Count, config.Agents.Count);

                        foreach (Records.IntentDefinition intent in config.Intents)
                        {
                            // The complement of the domain is not part of it: it is what a request
                            // matching no desk is, the classifier's own word, described in the
                            // classifier's own prompt. A domain that still declares it is one written
                            // before that was true, so the declaration is dropped rather than fought
                            // over — every reader downstream gets the framework's, exactly once.
                            if (string.Equals(intent.Name, Constants.Intents.Other, StringComparison.OrdinalIgnoreCase))
                            {
                                throw new InvalidOperationException(
                                    $"Plugin '{declaringAssembly}' declares the intent '{intent.Name}', which is reserved. It is the complement "
                                    + "of your domain rather than a part of it: the classifier carries it and describes it in its own prompt. "
                                    + "Delete the declaration.");
                            }

                            if (declaringAssemblyByIntent.TryGetValue(intent.Name, out string? firstAssembly))
                            {
                                throw new InvalidOperationException(
                                    $"The intent '{intent.Name}' is declared by two plugins, '{firstAssembly}' and '{declaringAssembly}'. "
                                    + "One name is one desk: deploy one of them, or rename the intent in the other.");
                            }

                            declaringAssemblyByIntent[intent.Name] = declaringAssembly;
                            mergedIntents.Add(intent);

                            // The intent list spelled out at startup. It is what the classifier will be
                            // given, the only place an operator reads it back before a conversation exists.
                            logger.LogInformation("   📋 Intent: {IntentName} - {IntentDescription}", intent.Name, intent.Description);
                        }

                        foreach (Records.Prompt prompt in config.Agents)
                        {
                            // Two prompts under one id would leave which desk answers to the order the
                            // assemblies happened to load in.
                            if (declaringAssemblyByPrompt.TryGetValue(prompt.ID, out string? firstAssembly))
                            {
                                throw new InvalidOperationException(
                                    $"The agent prompt '{prompt.ID}' is declared by two plugins, '{firstAssembly}' and '{declaringAssembly}'. "
                                    + "One id is one desk: deploy one of them, or rename the prompt in the other.");
                            }

                            declaringAssemblyByPrompt[prompt.ID] = declaringAssembly;
                            mergedAgents.Add(prompt);
                        }
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

        // Something was found, whether in one plugin or several: what the installation serves is all
        // of it together and nothing downstream can tell how many files it arrived in.
        if (mergedIntents.Count > 0 || mergedAgents.Count > 0)
        {
            logger.LogInformation(
                "✅ Domain assembled from {AssembliesCount} plugin(s): {IntentsCount} intents, {AgentsCount} agent prompts",
                declaringAssemblyByIntent.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count(), mergedIntents.Count, mergedAgents.Count);

            return new AgentConfiguration(mergedIntents, mergedAgents);
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