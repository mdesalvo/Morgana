namespace Morgana.AI.Interfaces;

/// <summary>
/// Service for discovering and resolving native tools based on intent.
/// Provides runtime tool discovery via ProvidesToolForIntentAttribute.
/// </summary>
public interface IToolRegistryService
{
    /// <summary>
    /// Finds the MorganaTool type that provides native tools for the specified intent.
    /// </summary>
    /// <param name="intent">The intent to find a tool for (case-insensitive)</param>
    /// <returns>The Type of the MorganaTool, or null if no tool found for this intent</returns>
    Type? FindToolTypeForIntent(string intent);
    
    /// <summary>
    /// Gets all registered tool types with their associated intents.
    /// </summary>
    /// <returns>Dictionary mapping intent names to tool types</returns>
    IReadOnlyDictionary<string, Type> GetAllRegisteredTools();
    
    /// <summary>
    /// Validates that all agents with HandlesIntentAttribute have corresponding native tools.
    /// Logs warnings for agents without native tools (they may rely only on MCP tools).
    /// </summary>
    /// <returns>Validation results with any warnings or errors</returns>
    Records.ToolRegistryValidationResult ValidateAgentToolMapping();
}