namespace Morgana.AI.Interfaces;

/// <summary>
/// Registry service for managing MCP server associations with Morgana agents.
/// Provides discovery and validation of agent-to-server mappings declared via UsesMCPServersAttribute.
/// </summary>
public interface IMCPServerRegistryService
{
    /// <summary>
    /// Get the list of MCP server names required by a specific agent type.
    /// </summary>
    /// <param name="agentType">The agent type to query</param>
    /// <returns>Array of MCP server names required by the agent (empty if none)</returns>
    string[] GetServerNamesForAgent(Type agentType);
    
    /// <summary>
    /// Get all agent types that declare MCP servers.
    /// </summary>
    /// <returns>Collection of agent types with UsesMCPServersAttribute</returns>
    IEnumerable<Type> GetAllAgentsWithMCPServers();
}