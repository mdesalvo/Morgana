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

        morganaPrompts = new Lazy<Records.Prompt[]>(LoadMorganaPrompts);
    }

    /// <summary>Gets all prompts merged from framework + domain sources (last-wins if ID duplication).</summary>
    /// <returns>Array of framework prompts + domain prompts</returns>
    public async Task<Records.Prompt[]> GetAllPromptsAsync()
    {
        // Merge: morgana.json + domain
        List<Records.Prompt> agentPrompts = await agentConfigService.GetAgentPromptsAsync();
        return [..morganaPrompts.Value, ..agentPrompts];
    }

    /// <summary>
    /// Resolves a prompt by ID (case-insensitive) from merged framework + domain sources.
    /// Framework IDs: Morgana, Classifier, Guard, Presentation; Domain IDs: intent names.
    /// </summary>
    /// <param name="promptID">Prompt identifier to resolve</param>
    /// <returns>Prompt matching the ID (case-insensitive)</returns>
    /// <exception cref="KeyNotFoundException">If ID not found in morgana.json or agents.json</exception>
    public async Task<Records.Prompt> ResolveAsync(string promptID)
    {
        Records.Prompt[] allPrompts = await GetAllPromptsAsync();

        Records.Prompt? prompt = allPrompts
            .SingleOrDefault(p => string.Equals(p.ID, promptID, StringComparison.OrdinalIgnoreCase));

        return prompt ?? throw new KeyNotFoundException($"Prompt with ID '{promptID}' not found in morgana.json or agents.json.");
    }

    /// <summary>
    /// Loads framework prompts from morgana.json embedded resource in Morgana.Agents assembly.
    /// Called once during service initialization for performance.
    /// </summary>
    /// <returns>Array of framework prompts (Morgana, Classifier, Guard, Presentation)</returns>
    /// <exception cref="FileNotFoundException">Thrown if morgana.json resource not found in assembly</exception>
    /// <remarks>
    /// <para><strong>Embedded Resource Loading:</strong></para>
    /// <list type="number">
    /// <item>Get executing assembly (Morgana.Agents.dll)</item>
    /// <item>Find manifest resource ending with ".morgana.json"</item>
    /// <item>Open resource stream</item>
    /// <item>Deserialize JSON to PromptCollection</item>
    /// <item>Extract Prompts array</item>
    /// <item>Cache in morganaPrompts field</item>
    /// </list>
    /// <para><strong>Resource Naming:</strong></para>
    /// <para>The resource name depends on the project structure and namespace.
    /// Typical format: "Morgana.Agents.morgana.json" or similar.
    /// The code uses EndsWith(".morgana.json") to be flexible with namespace variations.</para>
    /// <para><strong>Error Cases:</strong></para>
    /// <list type="bullet">
    /// <item>morgana.json not embedded as resource → FileNotFoundException</item>
    /// <item>Invalid JSON format → JsonException during deserialization</item>
    /// <item>Missing Prompts property → Returns empty array</item>
    /// </list>
    /// </remarks>
    private static Records.Prompt[] LoadMorganaPrompts()
    {
        // Load only morgana.json (framework prompts: Morgana, Classifier, Guard, Presentation)
        Assembly assembly = Assembly.GetExecutingAssembly();
        string resourceName = assembly.GetManifestResourceNames()
            .First(n => n.EndsWith(".morgana.json", StringComparison.OrdinalIgnoreCase));

        using Stream? stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException("Resource morgana.json not found in Morgana.Agents assembly.");

        Records.PromptCollection? promptsCollection = JsonSerializer.Deserialize<Records.PromptCollection>(
            stream, Records.DefaultJsonSerializerOptions);

        return promptsCollection?.Prompts ?? [];
    }
}