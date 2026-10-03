using System.Reflection;
using System.Text.Json;
using Morgana.AI.Interfaces;

namespace Morgana.AI.Services;

/// <summary>
/// Resolves prompts from two sources: morgana.json (framework) + IAgentConfigurationService (domain).
/// Two-tier architecture: framework prompts provide base system behavior; domain prompts specialize it per agent.
/// An ID found in both sources (case-insensitive lookup) is refused, never resolved in favour of either.
/// </summary>
public class ConfigurationPromptResolverService : IPromptResolverService
{
    /// <summary>
    /// Framework prompts loaded from morgana.json embedded resource
    /// </summary>
    private readonly Lazy<Records.Prompt[]> morganaPrompts;

    /// <summary>Domain prompts loaded from agents.json embedded resource</summary>
    private readonly Lazy<Task<List<Records.Prompt>>> agentPrompts;

    /// <summary>Initializes a new instance of ConfigurationPromptResolverService, deferring both layers to the first resolution.</summary>
    /// <param name="agentConfigService">Source of the domain agent prompts</param>
    public ConfigurationPromptResolverService(IAgentConfigurationService agentConfigService)
    {
        morganaPrompts = new Lazy<Records.Prompt[]>(LoadMorganaPrompts);
        agentPrompts = new Lazy<Task<List<Records.Prompt>>>(agentConfigService.GetAgentPromptsAsync);
    }

    /// <summary>Resolves a prompt by ID (case-insensitive) from merged framework + domain sources.</summary>
    /// <param name="promptID">Framework ID (Morgana, Classifier, Guard, ToolGuard, PeerGuard, Presentation, ChannelAdapter) or a domain intent name.</param>
    /// <exception cref="KeyNotFoundException">ID not found in morgana.json or agents.json.</exception>
    /// <exception cref="InvalidOperationException">ID declared both in morgana.json and in agents.json.</exception>
    public async Task<Records.Prompt> ResolveAsync(string promptID)
    {
        // Both layers merged into one lookup: a framework id and a domain intent are found the same way,
        // which is what lets an agent's prompt be reached by its intent name alone.
        Records.Prompt[] allPrompts = [..morganaPrompts.Value, ..await agentPrompts.Value];

        // A framework id and a domain intent name are meant to be disjoint vocabularies. An intent
        // named "guard" or "classifier" collides with a framework prompt: that has to fail loudly
        // here, never let one of the two win the lookup while the other becomes unreachable.
        Records.Prompt? prompt = allPrompts.SingleOrDefault(p => string.Equals(p.ID, promptID, StringComparison.OrdinalIgnoreCase));

        return prompt ?? throw new KeyNotFoundException($"Prompt with ID '{promptID}' not found in morgana.json or agents.json.");
    }

    /// <summary>
    /// Loads framework prompts from morgana.json, embedded as a resource in this very assembly.
    /// Called once (via the Lazy&lt;&gt; above), the first time any prompt is resolved.
    /// </summary>
    /// <returns>Array of framework prompts (Morgana, Classifier, Guard, ToolGuard, PeerGuard, Presentation, ChannelAdapter)</returns>
    /// <exception cref="FileNotFoundException">morgana.json is not embedded in this assembly.</exception>
    private static Records.Prompt[] LoadMorganaPrompts()
    {
        Assembly assembly = Assembly.GetExecutingAssembly();

        // The manifest name MSBuild generates is namespace-prefixed ("Morgana.AI.morgana.json"), so
        // matching the file name alone survives a change of root namespace or assembly name. The file
        // is still found by the name it was authored under.
        string resourceName = assembly.GetManifestResourceNames()
            .First(n => n.EndsWith(".morgana.json", StringComparison.OrdinalIgnoreCase));

        // The framework prompts ship inside this very assembly, so their absence is a broken build
        // rather than a deployment that forgot a file.
        using Stream? stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException("Resource morgana.json not found in the Morgana.AI assembly.");

        // The whole framework layer as morgana.json declares it: every prompt with its four sections,
        // the global policies, the injection templates, the error answers.
        Records.PromptCollection? promptsCollection = JsonSerializer.Deserialize<Records.PromptCollection>(
            stream, Records.DefaultJsonSerializerOptions);

        // A null collection (empty/malformed JSON body) degrades to an empty prompt array rather
        // than throwing — every consumer of ResolveAsync already has to handle
        // "prompt ID not found" as a real, expected outcome (see ResolveAsync's KeyNotFoundException
        // above), so an empty framework layer surfaces through that exact same, already-handled path
        // instead of needing a second failure mode of its own.
        return promptsCollection?.Prompts ?? [];
    }
}