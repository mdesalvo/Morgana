namespace Morgana.AI.Interfaces;

/// <summary>
/// Client-side interface for connecting to remote MCP servers via HTTP.
/// NOT IMPLEMENTED IN v0.5.0 - reserved for future use.
/// 
/// Future implementation will enable:
/// - Discovery of remote MCP servers
/// - Invocation of tools on external MCP endpoints
/// - Integration with third-party MCP providers
/// </summary>
public interface IMCPClient
{
    /// <summary>
    /// Discover tools from a remote MCP server via HTTP.
    /// </summary>
    /// <param name="serverUrl">MCP server endpoint URL</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Collection of available MCP tools</returns>
    Task<IEnumerable<Records.MCPToolDefinition>> DiscoverToolsAsync(
        string serverUrl, 
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Invoke a tool on a remote MCP server via HTTP.
    /// </summary>
    /// <param name="serverUrl">MCP server endpoint URL</param>
    /// <param name="toolName">Tool identifier</param>
    /// <param name="parameters">Tool input parameters</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Tool execution result or error</returns>
    Task<Records.MCPToolResult> InvokeRemoteToolAsync(
        string serverUrl, 
        string toolName, 
        Dictionary<string, object> parameters,
        CancellationToken cancellationToken = default);
}

// Future implementation (v0.6+):
// 
// public class MCPClient : IMCPClient
// {
//     private readonly HttpClient httpClient;
//     
//     public async Task<IEnumerable<MCPToolDefinition>> DiscoverToolsAsync(string serverUrl, ...)
//     {
//         // HTTP GET {serverUrl}/tools
//         // Parse JSON response into MCPToolDefinition[]
//     }
//     
//     public async Task<MCPToolResult> InvokeRemoteToolAsync(string serverUrl, string toolName, ...)
//     {
//         // HTTP POST {serverUrl}/tools/{toolName}
//         // Send parameters as JSON body
//         // Parse response into MCPToolResult
//     }
// }
