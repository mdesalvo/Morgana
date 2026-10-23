using System.Reflection;
using System.Text.Json;
using Morgana.AI.Interfaces;

namespace Morgana.AI.Services;

/// <summary>
/// Resolves prompts from two sources: morgana.json (framework) + IAgentConfigurationService (domain).
/// Two-tier architecture: framework prompts provide base system behavior; domain prompts specialize it.
/// The two vocabularies are disjoint: an id declared in both layers is refused rather than resolved, so neither layer hides the other.
/// </summary>
public class ConfigurationPromptResolverService : IPromptResolverService
{
    /// <summary>
    /// Framework prompts loaded from morgana.json embedded resource.
    /// Cached at service initialization for performance.
    /// </summary>
    private readonly Lazy<Records.Prompt[]> morganaPrompts;

    /// <summary>
    /// Domain prompts, read once on first resolution whatever caching the <see cref="IAgentConfigurationService"/> implementation does or does not do.
    /// </summary>
    private readonly Lazy<Task<List<Records.Prompt>>> agentPrompts;

    /// <summary>
    /// Initializes a new instance of ConfigurationPromptResolverService.
    /// Defers the reads of both layers until the first prompt is resolved.
    /// </summary>
    /// <param name="agentConfigService">Service for loading domain agent prompts</param>
    public ConfigurationPromptResolverService(IAgentConfigurationService agentConfigService)
    {
        // The domain layer is read at the first resolution and never again on later lookups.
        agentPrompts = new Lazy<Task<List<Records.Prompt>>>(agentConfigService.GetAgentPromptsAsync);

        // Lazy<> rather than loading eagerly in the constructor: morgana.json never changes after
        // deployment, so the embedded-resource read + JSON parse in LoadMorganaPrompts only ever
        // needs to happen once, on whichever thread first asks for a prompt — not on every DI
        // resolution of this singleton and not before it's actually needed.
        morganaPrompts = new Lazy<Records.Prompt[]>(LoadMorganaPrompts);
    }

    /// <summary>Resolves a prompt by ID (case-insensitive) from merged framework + domain sources.</summary>
    /// <param name="promptID">Framework ID (Morgana, Classifier, Guard, Presentation, ChannelAdapter) or a domain intent name.</param>
    /// <exception cref="KeyNotFoundException">ID not found in morgana.json or agents.json.</exception>
    /// <exception cref="InvalidOperationException">ID declared in both morgana.json and agents.json.</exception>
    public async Task<Records.Prompt> ResolveAsync(string promptID)
    {
        // One lookup for both layers: a framework id and a domain intent are found the same way,
        // which is what lets an agent's prompt be reached by its intent name alone.
        Records.Prompt[] allPrompts = [..morganaPrompts.Value, ..await agentPrompts.Value];

        // A framework id and a domain intent name are meant to be disjoint vocabularies. An intent
        // named "guard" or "classifier" collides with a framework prompt: that has to fail loudly
        // here, never let one of the two win the lookup while the other becomes unreachable.
        Records.Prompt? prompt = allPrompts
            .SingleOrDefault(p => string.Equals(p.ID, promptID, StringComparison.OrdinalIgnoreCase));

        // An unknown id is a topology fault that the caller must see, never an empty prompt.
        return prompt ?? throw new KeyNotFoundException($"Prompt with ID '{promptID}' not found in morgana.json or agents.json.");
    }

    /// <summary>
    /// Loads framework prompts from morgana.json, embedded as a resource in this very assembly.
    /// Called once (via the Lazy&lt;&gt; above), the first time any prompt is resolved.
    /// </summary>
    /// <returns>Array of framework prompts (Morgana, Classifier, Guard, Presentation, ChannelAdapter)</returns>
    /// <exception cref="FileNotFoundException">morgana.json is not embedded in this assembly.</exception>
    private static Records.Prompt[] LoadMorganaPrompts()
    {
        // The framework layer is embedded in the assembly that this class belongs to.
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

        // An empty body leaves the framework layer empty: every lookup then fails as "prompt not found", the one failure that callers already handle.
        return promptsCollection?.Prompts ?? [];
    }
}