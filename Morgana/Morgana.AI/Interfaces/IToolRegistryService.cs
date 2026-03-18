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
    Type? FindToolTypeForIntent(string intent);

    /// <summary>
    /// Gets all registered tool types with their associated intents.
    /// Returns a snapshot of the tool registry for diagnostics and validation.
    /// </summary>
    /// <returns>
    /// Read-only dictionary mapping intent names to their corresponding MorganaTool types.
    /// </returns>
    IReadOnlyDictionary<string, Type> GetAllRegisteredTools();
}
