namespace Morgana.AI.Interfaces;

/// <summary>
/// Service for loading domain-specific agent configuration from external sources.
/// Provides separation between framework configuration (morgana.json) and domain configuration (agents.json).
/// </summary>
/// <remarks>
/// <para><strong>Purpose:</strong></para>
/// <para>This service enables a clean separation of concerns:</para>
/// <list type="bullet">
/// <item><term>Framework Config (morgana.json)</term><description>Core Morgana behavior, global policies, system prompts</description></item>
/// <item><term>Domain Config (agents.json)</term><description>Business-specific intents, agents, tools, and prompts</description></item>
/// </list>
/// <para><strong>Implementation Strategy:</strong></para>
/// <para>The default implementation (EmbeddedAgentConfigurationService) loads configuration from
/// an embedded agents.json resource in the plugin assembly. Alternative implementations could load from:</para>
/// <list type="bullet">
/// <item>Database (SQL, MongoDB, etc.)</item>
/// <item>External API or configuration service</item>
/// <item>File system (for development scenarios)</item>
/// <item>Cloud storage (Azure Blob, S3, etc.)</item>
/// </list>
/// <para><strong>Configuration Lifecycle:</strong></para>
/// <code>
/// 1. Application startup
/// 2. PluginLoaderService loads domain assemblies
/// 3. IAgentConfigurationService registered in DI
/// 4. Configuration loaded from agents.json (or other source)
/// 5. Intents used by ClassifierActor and presentation
/// 6. Agent prompts used by AgentAdapter during agent creation
/// </code>
/// <para><strong>Usage Example:</strong></para>
/// <code>
/// // In Program.cs
/// builder.Services.AddSingleton&lt;IAgentConfigurationService, EmbeddedAgentConfigurationService&gt;();
/// 
/// // In ConversationSupervisorActor
/// List&lt;IntentDefinition&gt; intents = await agentConfigService.GetIntentsAsync();
/// // Used for presentation generation
/// 
/// // In AgentAdapter
/// Prompt agentPrompt = await promptResolverService.ResolveAsync("billing");
/// // Loaded via IAgentConfigurationService
/// </code>
/// </remarks>
public interface IAgentConfigurationService
{
    /// <summary>
    /// Loads intent definitions from domain configuration (typically agents.json).
    /// Returns intent definitions used by ClassifierActor for intent classification and
    /// by ConversationSupervisorActor for presentation generation.
    /// </summary>
    /// <returns>
    /// List of intent definitions containing Name, Description, Label, and DefaultValue.
    /// Returns empty list if configuration source not found or contains no intents.
    /// </returns>
    /// <remarks>
    /// <para><strong>Intent Definition Structure:</strong></para>
    /// <code>
    /// {
    ///   "Name": "billing",                        // Intent identifier (lowercase)
    ///   "Description": "requests to view invoices, extract or explain invoice details",
    ///   "Label": "📄 Billing",                    // User-facing label with emoji
    ///   "DefaultValue": "I would like to check my invoices"  // Sample user message
    /// }
    /// </code>
    /// <para><strong>Usage Scenarios:</strong></para>
    /// <list type="bullet">
    /// <item><term>ClassifierActor</term><description>Formats intents for LLM classification prompt</description></item>
    /// <item><term>Presentation</term><description>Generates quick reply buttons from displayable intents</description></item>
    /// <item><term>Validation</term><description>Ensures all required intents are configured</description></item>
    /// </list>
    /// <para><strong>Error Handling:</strong></para>
    /// <para>Returns empty list rather than throwing exceptions to support graceful degradation.
    /// The system logs warnings and can operate with no domain agents (using only "other" intent).</para>
    /// </remarks>
    Task<List<Records.IntentDefinition>> GetIntentsAsync();
    
    /// <summary>
    /// Loads agent prompt configurations from domain configuration (typically agents.json).
    /// Returns prompts used by AgentAdapter to compose full agent instructions during agent creation.
    /// </summary>
    /// <returns>
    /// List of agent prompts containing Content, Instructions, Personality, Tools, and metadata.
    /// Returns empty list if configuration source not found or contains no agent prompts.
    /// </returns>
    /// <remarks>
    /// <para><strong>Agent Prompt Structure:</strong></para>
    /// <code>
    /// {
    ///   "ID": "Billing",
    ///   "Type": "INTENT",
    ///   "SubType": "AGENT",
    ///   "Content": "You know the book of spells called 'Billing and Payments'...",
    ///   "Instructions": "Never invent procedures you don't explicitly possess...",
    ///   "Personality": "You are a formal and pragmatic witch...",
    ///   "Language": "en-US",
    ///   "Version": "1",
    ///   "AdditionalProperties": [
    ///     {
    ///       "Tools": [ /* Tool definitions */ ]
    ///     }
    ///   ]
    /// }
    /// </code>
    /// <para><strong>Prompt Composition Flow:</strong></para>
    /// <list type="number">
    /// <item>AgentAdapter receives agent type (e.g., BillingAgent)</item>
    /// <item>Extracts intent from [HandlesIntent] attribute</item>
    /// <item>Calls IPromptResolverService.ResolveAsync(intent)</item>
    /// <item>PromptResolverService queries IAgentConfigurationService</item>
    /// <item>Returns matching agent prompt from domain configuration</item>
    /// <item>AgentAdapter merges with Morgana framework prompt</item>
    /// <item>Final instructions passed to AIAgent creation</item>
    /// </list>
    /// <para><strong>Multi-Source Support:</strong></para>
    /// <para>Implementations can aggregate prompts from multiple sources (embedded resources,
    /// external files, databases) to support modular domain configurations.</para>
    /// </remarks>
    Task<List<Records.Prompt>> GetAgentPromptsAsync();
}