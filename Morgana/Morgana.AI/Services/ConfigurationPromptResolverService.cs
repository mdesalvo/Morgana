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
        // resolution of this singleton, and not before it's actually needed.
        morganaPrompts = new Lazy<Records.Prompt[]>(LoadMorganaPrompts);
    }

    /// <summary>Gets all prompts merged from framework + domain sources (last-wins if ID duplication).</summary>
    /// <returns>Array of framework prompts + domain prompts</returns>
    public async Task<Records.Prompt[]> GetAllPromptsAsync()
    {
        // Framework prompts first, domain prompts appended after: ResolveAsync below picks the
        // FIRST id match via SingleOrDefault, so this ordering alone doesn't decide precedence —
        // see ResolveAsync's own comment for why a domain/framework ID collision is actually an
        // ambiguity error, not a silent override, despite what "domain prompts override" might imply.
        List<Records.Prompt> agentPrompts = await agentConfigService.GetAgentPromptsAsync();
        return [..morganaPrompts.Value, ..agentPrompts];
    }

    /// <summary>Resolves a prompt by ID (case-insensitive) from merged framework + domain sources.</summary>
    /// <param name="promptID">Framework ID (Morgana, Classifier, Guard, Presentation) or a domain intent name.</param>
    /// <exception cref="KeyNotFoundException">ID not found in morgana.json or agents.json.</exception>
    public async Task<Records.Prompt> ResolveAsync(string promptID)
    {
        Records.Prompt[] allPrompts = await GetAllPromptsAsync();

        // SingleOrDefault, not FirstOrDefault: framework IDs ("Morgana", "Classifier", "Guard",
        // "Presentation") and domain IDs (intent names from agents.json) are meant to be disjoint
        // vocabularies. If a plugin author names an intent "guard" or "classifier", that collides
        // with a framework prompt and SingleOrDefault throws InvalidOperationException — loud and
        // at prompt-resolution time — rather than silently letting whichever one happens to come
        // first in the merged array win and the other become permanently unreachable.
        Records.Prompt? prompt = allPrompts
            .SingleOrDefault(p => string.Equals(p.ID, promptID, StringComparison.OrdinalIgnoreCase));

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
        Assembly assembly = Assembly.GetExecutingAssembly();

        // EndsWith rather than an exact resource name: the MSBuild-generated manifest resource
        // name is namespace-prefixed (typically "Morgana.AI.morgana.json"), and matching only the
        // file-name suffix means this keeps working even if the root namespace or the assembly
        // name itself changes — the file is still found by its own name.
        string resourceName = assembly.GetManifestResourceNames()
            .First(n => n.EndsWith(".morgana.json", StringComparison.OrdinalIgnoreCase));

        using Stream? stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException("Resource morgana.json not found in Morgana.Agents assembly.");

        Records.PromptCollection? promptsCollection = JsonSerializer.Deserialize<Records.PromptCollection>(
            stream, Records.DefaultJsonSerializerOptions);

        // A null collection (empty/malformed JSON body) degrades to an empty prompt array rather
        // than throwing — every consumer of GetAllPromptsAsync/ResolveAsync already has to handle
        // "prompt ID not found" as a real, expected outcome (see ResolveAsync's KeyNotFoundException
        // above), so an empty framework layer surfaces through that exact same, already-handled path
        // instead of needing a second failure mode of its own.
        return promptsCollection?.Prompts ?? [];
    }
}