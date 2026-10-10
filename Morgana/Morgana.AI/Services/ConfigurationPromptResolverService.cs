using System.Reflection;
using System.Text.Json;
using Morgana.AI.Interfaces;

namespace Morgana.AI.Services;

/// <summary>
/// Resolves prompts from two sources: morgana.json (framework) + IAgentConfigurationService (domain).
/// Two-tier architecture: framework prompts provide base system behavior; domain prompts override/specialize.
/// Domain prompts take precedence if same ID exists in both sources (case-insensitive lookup).
/// </summary>
public class ConfigurationPromptResolverService : IPromptResolverService
{
    /// <summary>
    /// Framework prompts loaded from morgana.json embedded resource.
    /// Cached at service initialization for performance.
    /// </summary>
    private readonly Lazy<Records.Prompt[]> morganaPrompts;

    /// <summary>
    /// Service for loading domain-specific agent prompts from agents.json or other sources.
    /// </summary>
    private readonly IAgentConfigurationService agentConfigService;

    /// <summary>
    /// Initializes a new instance of ConfigurationPromptResolverService.
    /// Loads framework prompts from morgana.json embedded resource.
    /// </summary>
    /// <param name="agentConfigService">Service for loading domain agent prompts</param>
    public ConfigurationPromptResolverService(IAgentConfigurationService agentConfigService)
    {
        this.agentConfigService = agentConfigService;

        // Lazy<> rather than loading eagerly in the constructor: morgana.json never changes after
        // deployment, so the embedded-resource read + JSON parse in LoadMorganaPrompts only ever
        // needs to happen once, on whichever thread first asks for a prompt — not on every DI
        // resolution of this singleton and not before it's actually needed.
        morganaPrompts = new Lazy<Records.Prompt[]>(LoadMorganaPrompts);
    }

    /// <summary>Gets all prompts merged from framework + domain sources (last-wins if ID duplication).</summary>
    /// <returns>Array of framework prompts + domain prompts</returns>
    public async Task<Records.Prompt[]> GetAllPromptsAsync()
    {
        // The domain layer is read at every call because the plugins may have been merged since the last one.
        List<Records.Prompt> agentPrompts = await agentConfigService.GetAgentPromptsAsync();
        // Framework prompts first: an id declared in both layers is not resolved by order but refused by ResolveAsync.
        return [..morganaPrompts.Value, ..agentPrompts];
    }

    /// <summary>Resolves a prompt by ID (case-insensitive) from merged framework + domain sources.</summary>
    /// <param name="promptID">Framework ID (Morgana, Classifier, Guard, Presentation) or a domain intent name.</param>
    /// <exception cref="KeyNotFoundException">ID not found in morgana.json or agents.json.</exception>
    public async Task<Records.Prompt> ResolveAsync(string promptID)
    {
        // Both layers already merged: a framework id and a domain intent are looked up the same way,
        // which is what lets an agent's prompt be reached by its intent name alone.
        Records.Prompt[] allPrompts = await GetAllPromptsAsync();

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
    /// <returns>Array of framework prompts (Morgana, Classifier, Guard, Presentation)</returns>
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