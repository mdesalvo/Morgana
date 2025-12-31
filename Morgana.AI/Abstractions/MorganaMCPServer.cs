using Microsoft.Extensions.Logging;
using Morgana.AI.Interfaces;

namespace Morgana.AI.Abstractions;

/// <summary>
/// Base class for Morgana MCP server implementations.
/// Implements IMCPServer for local/in-process tool providers.
/// Provides common infrastructure including error handling and logging.
/// </summary>
public abstract class MorganaMCPServer : IMCPServer
{
    protected readonly ILogger<MorganaMCPServer> logger;
    protected readonly Records.MCPServerConfig config;
    
    public string ServerName => config.Name;
    
    protected MorganaMCPServer(
        Records.MCPServerConfig config,
        ILogger<MorganaMCPServer> logger)
    {
        this.config = config;
        this.logger = logger;
    }
    
    /// <summary>
    /// Register available tools - implement in derived class.
    /// Define tool schemas with parameters and descriptions.
    /// </summary>
    /// <returns>Collection of tool definitions</returns>
    protected abstract Task<IEnumerable<Records.MCPToolDefinition>> RegisterToolsAsync();
    
    /// <summary>
    /// Execute specific tool - implement in derived class.
    /// Perform actual tool logic and return results.
    /// </summary>
    /// <param name="toolName">Tool identifier</param>
    /// <param name="parameters">Tool input parameters</param>
    /// <returns>Tool execution result</returns>
    protected abstract Task<Records.MCPToolResult> ExecuteToolAsync(
        string toolName, 
        Dictionary<string, object> parameters);

    // IMCPServer implementation with error handling

    public async Task<IEnumerable<Records.MCPToolDefinition>> ListToolsAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            logger.LogInformation($"Listing tools from MCP server: {ServerName}");
            IEnumerable<Records.MCPToolDefinition> tools = await RegisterToolsAsync();
            logger.LogInformation($"Found {tools.Count()} tools in server: {ServerName}");
            return tools;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Error listing tools from MCP server: {ServerName}");
            return [];
        }
    }

    public async Task<Records.MCPToolResult> CallToolAsync(
        string toolName, 
        Dictionary<string, object> parameters,
        CancellationToken cancellationToken = default)
    {
        try
        {
            logger.LogInformation($"🎯 CallToolAsync: {toolName}");
            logger.LogInformation($"   Parameters: {string.Join(", ", parameters.Select(p => $"{p.Key}={p.Value}"))}");
        
            Records.MCPToolResult result = await ExecuteToolAsync(toolName, parameters);
        
            logger.LogInformation($"   Result: IsError={result.IsError}, ContentLength={result.Content?.Length ?? 0}");
        
            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Exception in CallToolAsync for {toolName}");
            return new Records.MCPToolResult(true, null, $"Exception: {ex.Message}");
        }
    }
    
    public virtual Task<Records.MCPServerInfo> GetServerInfoAsync(
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new Records.MCPServerInfo(
            Name: ServerName,
            Version: "1.0.0",
            Capabilities: ["tools"]));
    }
}