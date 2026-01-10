namespace Morgana.AI.Interfaces;

/// <summary>
/// Service for discovering and resolving native tool implementations based on intent.
/// Provides runtime tool discovery via [ProvidesToolForIntent] attribute scanning.
/// </summary>
/// <remarks>
/// <para><strong>Purpose:</strong></para>
/// <para>This service enables automatic discovery of tool implementations without hardcoded mappings.
/// It scans loaded assemblies (including plugins) for classes decorated with [ProvidesToolForIntent]
/// attribute and builds a runtime registry for MorganaAgentAdapter consumption.</para>
/// <para><strong>Tool Discovery Flow:</strong></para>
/// <code>
/// 1. Application startup
/// 2. PluginLoaderService loads domain assemblies
/// 3. IToolRegistryService scans all loaded assemblies
/// 4. Finds classes with [ProvidesToolForIntent] attribute
/// 5. Builds intent → Type mapping dictionary
/// 6. MorganaAgentAdapter queries registry during agent creation
/// 7. Creates tool instances and registers methods in MorganaToolAdapter
/// </code>
/// <para><strong>Integration with MorganaAgentAdapter:</strong></para>
/// <code>
/// // MorganaAgentAdapter.CreateToolAdapterForIntent
/// Type? toolType = toolRegistryService?.FindToolTypeForIntent("billing");
/// if (toolType != null)
/// {
///     // Found: typeof(BillingTool) decorated with [ProvidesToolForIntent("billing")]
///     MorganaTool toolInstance = (MorganaTool)Activator.CreateInstance(
///         toolType,
///         logger,
///         () => contextProvider
///     );
///
///     // Register tool methods in MorganaToolAdapter
///     RegisterToolsInAdapter(toolAdapter, toolInstance, tools);
/// }
/// </code>
/// <para><strong>Plugin Support:</strong></para>
/// <para>This design enables plugin tools to be discovered and registered automatically without
/// modifying the core Morgana framework. Simply adding a plugin assembly with [ProvidesToolForIntent]
/// tools makes them available for agent use.</para>
/// <para><strong>Default Implementation:</strong></para>
/// <para>ProvidesToolForIntentRegistryService scans AppDomain.CurrentDomain.GetAssemblies() at startup
/// to build the registry. Alternative implementations could use lazy loading or dynamic assembly loading.</para>
/// </remarks>
public interface IToolRegistryService
{
    /// <summary>
    /// Finds the MorganaTool type that provides native tools for the specified intent.
    /// Uses [ProvidesToolForIntent] attribute to discover tool implementations.
    /// </summary>
    /// <param name="intent">The intent to find a tool for (e.g., "billing", "contract")</param>
    /// <returns>
    /// Type of the MorganaTool class decorated with [ProvidesToolForIntent(intent)],
    /// or null if no tool implementation found for this intent.
    /// </returns>
    /// <remarks>
    /// <para><strong>Resolution Logic:</strong></para>
    /// <list type="number">
    /// <item>Normalize intent (typically lowercase, case-insensitive matching)</item>
    /// <item>Look up in internal intent → Type dictionary</item>
    /// <item>Return Type if found, null otherwise</item>
    /// </list>
    /// <para><strong>Usage Example:</strong></para>
    /// <code>
    /// Type? toolType = toolRegistryService.FindToolTypeForIntent("billing");
    ///
    /// if (toolType != null)
    /// {
    ///     // Found: typeof(BillingTool)
    ///     // [ProvidesToolForIntent("billing")]
    ///     // public class BillingTool : MorganaTool
    ///
    ///     // Instantiate tool
    ///     MorganaTool tool = (MorganaTool)Activator.CreateInstance(
    ///         toolType,
    ///         logger,
    ///         () => contextProvider
    ///     );
    ///
    ///     // Register tool methods
    ///     RegisterToolsInAdapter(toolAdapter, tool, toolDefinitions);
    /// }
    /// else
    /// {
    ///     // No native tool found for "billing" intent
    ///     // Agent will only have framework tools (GetContextVariable, SetContextVariable)
    ///     logger.LogInformation("No native tool found for intent 'billing'");
    /// }
    /// </code>
    /// <para><strong>Case Sensitivity:</strong></para>
    /// <para>Intent matching is typically case-insensitive in implementations to prevent configuration
    /// errors. Best practice is to use lowercase intent names consistently.</para>
    /// <para><strong>Null Handling:</strong></para>
    /// <para>Returning null (rather than throwing) allows agents to operate with only framework tools
    /// if no domain-specific tool implementation exists. This is valid for simple conversational agents
    /// that don't need backend integrations.</para>
    /// </remarks>
    Type? FindToolTypeForIntent(string intent);

    /// <summary>
    /// Gets all registered tool types with their associated intents.
    /// Returns a snapshot of the tool registry for diagnostics and validation.
    /// </summary>
    /// <returns>
    /// Read-only dictionary mapping intent names to their corresponding MorganaTool types.
    /// </returns>
    /// <remarks>
    /// <para><strong>Usage Scenarios:</strong></para>
    /// <list type="bullet">
    /// <item><term>Startup Diagnostics</term><description>Log available tools at application startup</description></item>
    /// <item><term>Configuration Validation</term><description>Verify intents in agents.json have tool implementations</description></item>
    /// <item><term>Admin UI</term><description>Display available tool capabilities</description></item>
    /// <item><term>Testing</term><description>Verify tool discovery and registration</description></item>
    /// </list>
    /// <para><strong>Example:</strong></para>
    /// <code>
    /// // Startup diagnostics
    /// IReadOnlyDictionary&lt;string, Type&gt; tools = toolRegistryService.GetAllRegisteredTools();
    /// logger.LogInformation($"Registered {tools.Count} tool implementations:");
    /// foreach (KeyValuePair&lt;string, Type&gt; kvp in tools)
    /// {
    ///     logger.LogInformation($"  - {kvp.Key}: {kvp.Value.Name}");
    /// }
    /// // Output:
    /// // Registered 3 tool implementations:
    /// //   - billing: BillingTool
    /// //   - contract: ContractTool
    /// //   - troubleshooting: TroubleshootingTool
    ///
    /// // Configuration validation
    /// List&lt;IntentDefinition&gt; intents = await agentConfigService.GetIntentsAsync();
    /// IReadOnlyDictionary&lt;string, Type&gt; tools = toolRegistryService.GetAllRegisteredTools();
    ///
    /// foreach (IntentDefinition intent in intents)
    /// {
    ///     if (!tools.ContainsKey(intent.Name))
    ///     {
    ///         logger.LogWarning($"Intent '{intent.Name}' configured but no tool implementation found");
    ///     }
    /// }
    /// </code>
    /// <para><strong>Read-Only Contract:</strong></para>
    /// <para>The returned dictionary is read-only to prevent external modification of the tool registry.
    /// Tool registration happens only during service initialization via assembly scanning.</para>
    /// <para><strong>Performance Note:</strong></para>
    /// <para>This method returns a snapshot of the registry. Implementations should cache the dictionary
    /// rather than re-scanning assemblies on every call.</para>
    /// </remarks>
    IReadOnlyDictionary<string, Type> GetAllRegisteredTools();
}