namespace Morgana.AI.Interfaces;

/// <summary>
/// Server-side interface for Model Context Protocol.
/// Represents a source that exposes MCP tools (local or in-process).
/// Implementations provide tool discovery and execution capabilities.
/// </summary>
public interface IMCPServer
{
    /// <summary>
    /// Server unique identifier
    /// </summary>
    string ServerName { get; }
    
    /// <summary>
    /// Discover available tools from this server.
    /// Returns tool definitions with parameter schemas.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Collection of available MCP tools</returns>
    Task<IEnumerable<Records.MCPToolDefinition>> ListToolsAsync(
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Invoke a specific tool on this server.
    /// Validates parameters and executes tool logic.
    /// </summary>
    /// <param name="toolName">Tool identifier</param>
    /// <param name="parameters">Tool input parameters</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Tool execution result or error</returns>
    Task<Records.MCPToolResult> CallToolAsync(
        string toolName, 
        Dictionary<string, object> parameters,
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Get server capabilities and metadata.
    /// Useful for server discovery and capability negotiation.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Server information</returns>
    Task<Records.MCPServerInfo> GetServerInfoAsync(
        CancellationToken cancellationToken = default);
}
