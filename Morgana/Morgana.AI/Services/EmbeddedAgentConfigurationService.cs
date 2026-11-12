using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Morgana.AI.Interfaces;

namespace Morgana.AI.Services;

/// <summary>
/// Merges the agents.json embedded in every loaded plugin into the one domain this installation serves; none found is an empty domain.
/// Several plugins may each bring part of a domain, but none may disagree: an intent or a prompt id declared twice describes two agents
/// answering to one name. Nor may a domain take a reserved name: the complement Other, the system's own name or a framework prompt id.
/// </summary>
public class EmbeddedAgentConfigurationService : IAgentConfigurationService
{
    /// <summary>
    /// The merged domain, scanned on first use.
    /// DI construction can happen before the plugins have finished loading, so the scan waits for the first question.
    /// </summary>
    private readonly Lazy<AgentConfiguration> agentConfiguration;

    /// <summary>
    /// Logs which plugins brought a domain and warns when none did.
    /// An agentless installation is legal, so this warning is its only signal.
    /// </summary>
    private readonly ILogger logger;

    /// <summary>Defers the scan for the domain until the first intent or prompt is asked for.</summary>
    public EmbeddedAgentConfigurationService(ILogger logger)
    {
        this.logger = logger;

        // The scan runs once, when the plugins are surely loaded.
        agentConfiguration = new Lazy<AgentConfiguration>(LoadAgentConfiguration);
    }

    /// <summary>Returns the domain's intents, empty when no plugin brought a domain.</summary>
    public Task<List<Records.IntentDefinition>> GetIntentsAsync()
    {
        // The first question runs the scan; the domain is in memory from then on.
        return Task.FromResult(agentConfiguration.Value.Intents);
    }

    /// <summary>Returns the domain's agent prompts, empty when no plugin brought a domain.</summary>
    public Task<List<Records.Prompt>> GetAgentPromptsAsync()
    {
        // The first question runs the scan; the domain is in memory from then on.
        return Task.FromResult(agentConfiguration.Value.Agents);
    }

    /// <summary>
    /// Scans every loaded assembly for an embedded agents.json and merges what it finds, empty when none carries one.
    /// An unreadable agents.json is logged and passed over, while a readable but wrong one stops startup.
    /// </summary>
    /// <exception cref="InvalidOperationException">Two assemblies claim one intent or one prompt id. A domain takes a reserved name.</exception>
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
                                                             .Where(candidate => !candidate.IsDynamic))
        {
            // Matched by file name alone, as morgana.json is: what a plugin author calls their root
            // namespace is their business.
            string? resourceName = assembly.GetManifestResourceNames()
                                           .FirstOrDefault(manifestName => manifestName.EndsWith(".agents.json", StringComparison.OrdinalIgnoreCase));

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
                    AgentConfiguration? domainConfiguration = JsonSerializer.Deserialize<AgentConfiguration>(
                        stream, Records.DefaultJsonSerializerOptions);

                    // An empty file brings nothing to merge.
                    if (domainConfiguration != null)
                    {
                        // The plugin named in every refusal, so the operator knows which file to fix.
                        string declaringAssembly = assembly.GetName().Name ?? resourceName;

                        logger.LogInformation(
                            "✅ Loaded {IntentsCount} intents and {AgentsCount} agent prompts from agents.json", domainConfiguration.Intents.Count, domainConfiguration.Agents.Count);

                        foreach (Records.IntentDefinition intent in domainConfiguration.Intents)
                        {
                            // The complement of the domain is not part of it: it is what a request matching no agent is,
                            // the classifier's own word, described in the classifier's own prompt.
                            // A domain declaring it is refused rather than quietly corrected.
                            if (string.Equals(intent.Name, Constants.Intents.Other, StringComparison.OrdinalIgnoreCase))
                            {
                                throw new InvalidOperationException(
                                    $"Plugin '{declaringAssembly}' declares the intent '{intent.Name}', which is reserved. It is the complement "
                                    + "of your domain rather than a part of it: the classifier carries it and describes it in its own prompt. "
                                    + "Delete the declaration.");
                            }

                            // The orchestrator answers under this name and its words are filed under
                            // it in the conversation record. An agent taking the name would take over
                            // that record. It would also become indistinguishable from the
                            // orchestrator to everyone who tells the two apart by name.
                            if (string.Equals(intent.Name, Constants.Morgana, StringComparison.OrdinalIgnoreCase))
                            {
                                throw new InvalidOperationException(
                                    $"Plugin '{declaringAssembly}' declares the intent '{intent.Name}', which is reserved: it names the "
                                    + "system itself, which answers under it and keeps its own side of the conversation under it. "
                                    + "Rename the intent.");
                            }

                            // An agent's prompt is looked up by its intent's name in the registry that holds the
                            // framework's prompts: the agent would be served the framework's prompt instead of its own.
                            if (Constants.Prompts.All.Contains(intent.Name, StringComparer.OrdinalIgnoreCase))
                            {
                                // Startup stops here, since the intent would be answered with a framework prompt.
                                throw new InvalidOperationException(
                                    $"Plugin '{declaringAssembly}' declares the intent '{intent.Name}', which is reserved: it is the id of one of the framework's prompts "
                                    + "and an agent's prompt is looked up by its intent's name in the same registry. Rename the intent.");
                            }

                            // Every intent is offered as a welcome button, which needs both the text written on it
                            // and the sentence that pressing it sends on the user's behalf.
                            if (string.IsNullOrWhiteSpace(intent.Label) || string.IsNullOrWhiteSpace(intent.DefaultValue))
                            {
                                throw new InvalidOperationException(
                                    $"Plugin '{declaringAssembly}' declares the intent '{intent.Name}' without a Label or a DefaultValue. "
                                    + "Both are required: the welcome offers every intent as a button that shows its Label and sends its DefaultValue.");
                            }

                            // Two plugins declaring one intent describe two agents answering to one name.
                            if (declaringAssemblyByIntent.TryGetValue(intent.Name, out string? firstAssembly))
                            {
                                throw new InvalidOperationException(
                                    $"The intent '{intent.Name}' is declared by two plugins, '{firstAssembly}' and '{declaringAssembly}'. "
                                    + "One name is one agent: deploy one of them or rename the intent in the other.");
                            }

                            // The intent joins the domain, remembered with the plugin that brought it.
                            declaringAssemblyByIntent[intent.Name] = declaringAssembly;
                            mergedIntents.Add(intent);

                            // The intent list spelled out at startup. It is what the classifier will be
                            // given, the only place an operator reads it back before a conversation exists.
                            logger.LogInformation("   📋 Intent: {IntentName} - {IntentDescription}", intent.Name, intent.Description);
                        }

                        foreach (Records.Prompt prompt in domainConfiguration.Agents)
                        {
                            // A prompt under a framework id would collide with the framework's own prompt in the one registry that resolves both.
                            if (Constants.Prompts.All.Contains(prompt.ID, StringComparer.OrdinalIgnoreCase))
                            {
                                // Startup stops here, since the two prompts could not both be resolved by that id.
                                throw new InvalidOperationException(
                                    $"Plugin '{declaringAssembly}' declares the agent prompt '{prompt.ID}', which is reserved: it is the id of one of the framework's prompts "
                                    + "and the two would collide in one registry. Rename the prompt.");
                            }

                            // Two prompts under one id would leave which agent answers to the order the
                            // assemblies happened to load in.
                            if (declaringAssemblyByPrompt.TryGetValue(prompt.ID, out string? firstAssembly))
                            {
                                throw new InvalidOperationException(
                                    $"The agent prompt '{prompt.ID}' is declared by two plugins, '{firstAssembly}' and '{declaringAssembly}'. "
                                    + "One id is one agent: deploy one of them or rename the prompt in the other.");
                            }

                            // The prompt joins the domain, remembered with the plugin that brought it.
                            declaringAssemblyByPrompt[prompt.ID] = declaringAssembly;
                            mergedAgents.Add(prompt);
                        }
                    }
                }
                // The refusals above are verdicts on a domain that is readable and wrong (a reserved
                // name, a name claimed twice); each is meant to stop startup where it is stated.
                // Swallowed here they would drop the whole domain instead. The operator would
                // meet the consequence much later, as a prompt that cannot be found for an intent
                // nobody refused: the wrong place to go looking.
                catch (Exception ex) when (ex is not InvalidOperationException)
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

            // The domain the classifier, the presenter and the prompt resolver all read.
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

    /// <summary>The shape of an agents.json: the intents the classifier routes on and the prompt of every agent that handles one.</summary>
    /// <param name="Intents">The intents, offered as welcome buttons and ranked by the classifier.</param>
    /// <param name="Agents">The agent prompts, each under its intent's name.</param>
    private record AgentConfiguration(
        List<Records.IntentDefinition> Intents,
        List<Records.Prompt> Agents);
}