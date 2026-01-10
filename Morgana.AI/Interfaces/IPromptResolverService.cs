using static Morgana.AI.Records;

namespace Morgana.AI.Interfaces;

/// <summary>
/// Service for resolving and loading prompt templates from configuration sources.
/// Provides access to framework prompts (morgana.json) and domain prompts (agents.json).
/// </summary>
/// <remarks>
/// <para><strong>Purpose:</strong></para>
/// <para>This service abstracts prompt storage and retrieval, enabling prompts to be loaded from
/// various sources (embedded resources, files, databases, APIs) without coupling actors and agents
/// to specific storage mechanisms.</para>
/// <para><strong>Prompt Sources:</strong></para>
/// <list type="bullet">
/// <item><term>Framework Prompts (morgana.json)</term><description>
/// Core system prompts: Morgana (base behavior), Classifier, Guard, Presentation
/// </description></item>
/// <item><term>Domain Prompts (agents.json)</term><description>
/// Business-specific agent prompts: Billing, Contract, Troubleshooting, etc.
/// </description></item>
/// </list>
/// <para><strong>Prompt Structure:</strong></para>
/// <code>
/// {
///   "ID": "Morgana",
///   "Type": "SYSTEM",
///   "SubType": "AGENT",
///   "Content": "You are a digital assistant...",
///   "Instructions": "Conversation in progress between assistant and user...",
///   "Personality": "Your name is Morgana...",
///   "Language": "en-US",
///   "Version": "1",
///   "AdditionalProperties": [
///     {
///       "GlobalPolicies": [ /* Policy definitions */ ],
///       "ErrorAnswers": [ /* Error message templates */ ],
///       "Tools": [ /* Tool definitions */ ]
///     }
///   ]
/// }
/// </code>
/// <para><strong>Default Implementation:</strong></para>
/// <para>ConfigurationPromptResolverService loads prompts from both morgana.json (framework)
/// and agents.json (domain) embedded resources, merging them into a unified prompt registry.</para>
/// <para><strong>Usage Patterns:</strong></para>
/// <code>
/// // Actors: Load framework prompts
/// Prompt guardPrompt = await promptResolverService.ResolveAsync("Guard");
/// Prompt classifierPrompt = await promptResolverService.ResolveAsync("Classifier");
///
/// // MorganaAgentAdapter: Load domain agent prompts
/// Prompt billingPrompt = await promptResolverService.ResolveAsync("billing");
/// Prompt contractPrompt = await promptResolverService.ResolveAsync("contract");
/// </code>
/// </remarks>
public interface IPromptResolverService
{
    /// <summary>
    /// Gets all available prompts from all configured sources.
    /// Returns both framework prompts (morgana.json) and domain prompts (agents.json).
    /// </summary>
    /// <returns>Array of all prompt definitions from merged sources</returns>
    /// <remarks>
    /// <para><strong>Use Cases:</strong></para>
    /// <list type="bullet">
    /// <item><term>Diagnostics</term><description>Log available prompts at startup</description></item>
    /// <item><term>Validation</term><description>Verify all expected prompts are configured</description></item>
    /// <item><term>Admin UI</term><description>Display available prompt templates</description></item>
    /// <item><term>Testing</term><description>Enumerate prompts for test coverage verification</description></item>
    /// </list>
    /// <para><strong>Prompt Merging:</strong></para>
    /// <para>The default implementation merges prompts from multiple sources. If the same prompt ID
    /// exists in both morgana.json and agents.json, the domain-specific version (agents.json) takes precedence.</para>
    /// <para><strong>Example:</strong></para>
    /// <code>
    /// Prompt[] allPrompts = await promptResolverService.GetAllPromptsAsync();
    /// logger.LogInformation($"Loaded {allPrompts.Length} prompts:");
    /// foreach (Prompt prompt in allPrompts)
    /// {
    ///     logger.LogInformation($"  - {prompt.ID} ({prompt.Type}/{prompt.SubType})");
    /// }
    /// // Output:
    /// // Loaded 7 prompts:
    /// //   - Morgana (SYSTEM/AGENT)
    /// //   - Classifier (SYSTEM/ACTOR)
    /// //   - Guard (SYSTEM/ACTOR)
    /// //   - Presentation (SYSTEM/PRESENTATION)
    /// //   - Billing (INTENT/AGENT)
    /// //   - Contract (INTENT/AGENT)
    /// //   - Troubleshooting (INTENT/AGENT)
    /// </code>
    /// </remarks>
    Task<Prompt[]> GetAllPromptsAsync();

    /// <summary>
    /// Resolves a specific prompt by its unique identifier.
    /// Searches across all configured prompt sources (framework and domain).
    /// </summary>
    /// <param name="promptID">
    /// Unique identifier of the prompt to resolve.
    /// Framework prompts: "Morgana", "Classifier", "Guard", "Presentation"
    /// Domain prompts: intent names like "billing", "contract", "troubleshooting"
    /// </param>
    /// <returns>Prompt definition matching the specified ID</returns>
    /// <exception cref="InvalidOperationException">Thrown if prompt ID not found in any source</exception>
    /// <remarks>
    /// <para><strong>ID Resolution Rules:</strong></para>
    /// <list type="bullet">
    /// <item>Case-sensitive matching (exact ID match required)</item>
    /// <item>Domain prompts (agents.json) override framework prompts (morgana.json) if IDs collide</item>
    /// <item>Throws exception if prompt not found (fail-fast for configuration errors)</item>
    /// </list>
    /// <para><strong>Usage Examples:</strong></para>
    /// <code>
    /// // Framework prompt resolution
    /// Prompt morganaPrompt = await promptResolverService.ResolveAsync("Morgana");
    /// // Returns: Core Morgana system prompt with personality, policies, context tools
    ///
    /// Prompt guardPrompt = await promptResolverService.ResolveAsync("Guard");
    /// // Returns: Content moderation prompt with profanity terms and policy checks
    ///
    /// // Domain prompt resolution
    /// Prompt billingPrompt = await promptResolverService.ResolveAsync("billing");
    /// // Returns: Billing agent prompt with tool definitions for invoice retrieval
    ///
    /// // Error case
    /// try
    /// {
    ///     Prompt unknown = await promptResolverService.ResolveAsync("nonexistent");
    /// }
    /// catch (InvalidOperationException ex)
    /// {
    ///     // Prompt 'nonexistent' not found in configuration
    ///     logger.LogError(ex, "Prompt resolution failed");
    /// }
    /// </code>
    /// <para><strong>Prompt Composition:</strong></para>
    /// <para>Resolved prompts contain all fields needed for agent/actor initialization:</para>
    /// <list type="bullet">
    /// <item><term>Content</term><description>Core prompt defining role and capabilities</description></item>
    /// <item><term>Instructions</term><description>Behavioral rules and guidelines</description></item>
    /// <item><term>Personality</term><description>Tone and character traits</description></item>
    /// <item><term>AdditionalProperties</term><description>Tools, policies, error messages, etc.</description></item>
    /// </list>
    /// </remarks>
    Task<Prompt> ResolveAsync(string promptID);
}