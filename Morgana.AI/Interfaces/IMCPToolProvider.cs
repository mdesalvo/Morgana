using Microsoft.Extensions.AI;

namespace Morgana.AI.Interfaces;

/// <summary>
/// Bridges MCP tools to Morgana's AIFunction system.
/// Orchestrates tool loading from multiple MCP servers and converts
/// MCP tool definitions to Microsoft.Extensions.AI format.
/// </summary>
public interface IMCPToolProvider
{
    /// <summary>
    /// Load all tools from a specific MCP server.
    /// Converts MCP tool definitions to AIFunction format for agent use.
    /// </summary>
    /// <param name="serverName">MCP server name from configuration</param>
    /// <returns>Collection of AIFunctions ready for agent registration</returns>
    Task<IEnumerable<AIFunction>> LoadToolsFromServerAsync(string serverName);
    
    /// <summary>
    /// Load tools from all configured and enabled MCP servers.
    /// Aggregates tools across multiple servers into single collection.
    /// </summary>
    /// <returns>Combined collection of AIFunctions from all servers</returns>
    Task<IEnumerable<AIFunction>> LoadAllToolsAsync();
    
    /// <summary>
    /// Get list of registered MCP servers.
    /// Useful for diagnostics and configuration validation.
    /// </summary>
    /// <returns>Collection of server names</returns>
    IEnumerable<string> GetRegisteredServers();
}
