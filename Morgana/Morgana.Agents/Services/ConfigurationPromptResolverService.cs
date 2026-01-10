using System.Reflection;
using System.Text.Json;
using Morgana.Foundations;
using Morgana.Foundations.Interfaces;

namespace Morgana.Agents.Services;

/// <summary>
/// Implementation of IPromptResolverService that resolves prompts from two sources:
/// 1. morgana.json (framework prompts: Morgana, Classifier, Guard, Presentation)
/// 2. IAgentConfigurationService (domain prompts: billing, contract, troubleshooting, etc.)
/// </summary>
/// <remarks>
/// <para><strong>Two-Tier Prompt Architecture:</strong></para>
/// <list type="bullet">
/// <item><term>Framework Prompts (morgana.json)</term><description>
/// Core Morgana system prompts embedded in Morgana.Agents assembly.
/// These define base behavior, policies, and framework tools.
/// Loaded once at service initialization via embedded resource.
/// </description></item>
/// <item><term>Domain Prompts (agents.json)</term><description>
/// Business-specific agent prompts loaded via IAgentConfigurationService.
/// These define specialized agent capabilities, tools, and behaviors.
/// Can be from plugins, databases, or other dynamic sources.
/// </description></item>
/// </list>
/// <para><strong>Prompt Resolution Priority:</strong></para>
/// <para>Domain prompts (agents.json) take precedence over framework prompts (morgana.json)
/// if the same prompt ID exists in both sources. This allows domain customization of framework prompts.</para>
/// <para><strong>Usage Pattern:</strong></para>
/// <code>
/// // Framework prompt
/// Prompt guardPrompt = await resolver.ResolveAsync("Guard");
/// // Loaded from: morgana.json (embedded in Morgana.Agents assembly)
///
/// // Domain prompt
/// Prompt billingPrompt = await resolver.ResolveAsync("billing");
/// // Loaded from: agents.json (via IAgentConfigurationService)
/// </code>
/// <para><strong>Dependency Injection:</strong></para>
/// <code>
/// // Program.cs
/// builder.Services.AddSingleton&lt;IAgentConfigurationService, EmbeddedAgentConfigurationService&gt;();
/// builder.Services.AddSingleton&lt;IPromptResolverService, ConfigurationPromptResolverService&gt;();
/// </code>
/// </remarks>
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

    /// <summary>
    /// Gets all prompts from both framework and domain sources.
    /// Merges morgana.json prompts with domain prompts from IAgentConfigurationService.
    /// </summary>
    /// <returns>Array of all available prompts (framework + domain)</returns>
    /// <remarks>
    /// <para><strong>Merge Order:</strong></para>
    /// <list type="number">
    /// <item>Framework prompts (morgana.json) - base layer</item>
    /// <item>Domain prompts (agents.json) - overlay layer</item>
    /// </list>
    /// <para>If a prompt ID exists in both sources, the domain version is used (last-wins merge).</para>
    /// <para><strong>Typical Result:</strong></para>
    /// <code>
    /// Framework prompts (4):
    ///   - Morgana (base system prompt)
    ///   - Classifier (intent classification)
    ///   - Guard (content moderation)
    ///   - Presentation (welcome message generation)
    ///
    /// Domain prompts (3):
    ///   - billing (Billing agent prompt)
    ///   - contract (Contract agent prompt)
    ///   - troubleshooting (Troubleshooting agent prompt)
    ///
    /// Total: 7 prompts
    /// </code>
    /// </remarks>
    public async Task<Records.Prompt[]> GetAllPromptsAsync()
    {
        // Merge: morgana.json + domain
        List<Records.Prompt> agentPrompts = await agentConfigService.GetAgentPromptsAsync();
        return [..morganaPrompts.Value, ..agentPrompts];
    }

    /// <summary>
    /// Resolves a specific prompt by ID, searching both framework and domain sources.
    /// </summary>
    /// <param name="promptID">
    /// Unique prompt identifier.
    /// Framework IDs: "Morgana", "Classifier", "Guard", "Presentation"
    /// Domain IDs: intent names like "billing", "contract", "troubleshooting"
    /// </param>
    /// <returns>Prompt matching the specified ID</returns>
    /// <exception cref="KeyNotFoundException">Thrown if prompt ID not found in any source</exception>
    /// <remarks>
    /// <para><strong>Resolution Algorithm:</strong></para>
    /// <list type="number">
    /// <item>Merge all prompts from morgana.json and agents.json</item>
    /// <item>Search for prompt with matching ID (case-insensitive)</item>
    /// <item>Return first match found</item>
    /// <item>Throw KeyNotFoundException if no match</item>
    /// </list>
    /// <para><strong>Case Insensitivity:</strong></para>
    /// <para>Prompt ID matching is case-insensitive, so "Morgana", "morgana", and "MORGANA" all resolve
    /// to the same prompt. This prevents configuration errors from case mismatches.</para>
    /// <para><strong>Error Handling:</strong></para>
    /// <para>Throws KeyNotFoundException rather than returning null to provide clear error messages
    /// for configuration issues. This fail-fast approach helps catch typos in prompt IDs during development.</para>
    /// </remarks>
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
            stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        return promptsCollection?.Prompts ?? [];
    }
}