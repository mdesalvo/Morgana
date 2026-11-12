using System.Reflection;
using System.Text.Json;
using Morgana.AI.Interfaces;

namespace Morgana.AI.Services;

/// <summary>
/// Resolves a prompt by id from the framework layer of morgana.json and the domain layer of the plugins' agents.json.
/// The two vocabularies are disjoint: an id declared in both layers is refused rather than resolved, so neither layer hides the other.
/// </summary>
public class ConfigurationPromptResolverService : IPromptResolverService
{
    /// <summary>
    /// The framework prompts of the embedded morgana.json, read on the first resolution.
    /// The file never changes after deployment, so it is read and parsed once.
    /// </summary>
    private readonly Lazy<Records.Prompt[]> morganaPrompts;

    /// <summary>The domain prompts, read once on the first resolution whatever caching the configuration service does.</summary>
    private readonly Lazy<Task<List<Records.Prompt>>> agentPrompts;

    /// <summary>Defers the reads of both layers until the first prompt is resolved.</summary>
    public ConfigurationPromptResolverService(IAgentConfigurationService agentConfigService)
    {
        // The domain layer is read at the first resolution and never again on later lookups.
        agentPrompts = new Lazy<Task<List<Records.Prompt>>>(agentConfigService.GetAgentPromptsAsync);

        // The framework layer is read at the first resolution, on whichever thread asks first.
        morganaPrompts = new Lazy<Records.Prompt[]>(LoadMorganaPrompts);
    }

    /// <summary>Resolves a prompt by ID (case-insensitive) from merged framework + domain sources.</summary>
    /// <param name="promptID">One of the seven framework ids or a domain intent name.</param>
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

        // An id found in neither layer is a configuration fault, named for whoever reads the log.
        return prompt ?? throw new KeyNotFoundException($"Prompt with ID '{promptID}' not found in morgana.json or agents.json.");
    }

    /// <summary>Reads the framework prompts from the morgana.json embedded in this assembly.</summary>
    /// <exception cref="FileNotFoundException">morgana.json is not embedded in this assembly.</exception>
    private static Records.Prompt[] LoadMorganaPrompts()
    {
        // morgana.json ships inside Morgana.AI itself.
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

        // An empty body leaves the framework layer empty, which surfaces as the KeyNotFoundException of the first
        // framework prompt resolved: one failure for a missing prompt, whatever made it missing.
        return promptsCollection?.Prompts ?? [];
    }
}