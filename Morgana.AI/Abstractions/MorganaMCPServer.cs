using System.Reflection;
using Microsoft.Extensions.Logging;
using Morgana.AI.Interfaces;

namespace Morgana.AI.Abstractions;

/// <summary>
/// Base class for Morgana MCP server implementations.
/// Implements IMCPServer for local/in-process tool providers.
/// Provides common infrastructure including error handling, logging,
/// and embedded resource management.
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
    /// Copy embedded database resource to disk if not exists.
    /// Called by derived classes during initialization.
    /// Extracts pre-generated SQLite databases from assembly resources.
    /// </summary>
    /// <param name="embeddedResourceName">Full resource name (e.g., "Namespace.File.db")</param>
    protected void EnsureDatabaseFromEmbeddedResource(string embeddedResourceName)
    {
        string targetPath = config.ConnectionString;
        
        // If database already exists on disk, skip
        if (File.Exists(targetPath))
        {
            logger.LogDebug($"Database already exists: {targetPath}");
            return;
        }
        
        // Ensure directory exists
        string? directory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        
        // Extract embedded resource to disk
        Assembly assembly = GetType().Assembly;
        using Stream? stream = assembly.GetManifestResourceStream(embeddedResourceName);
        
        if (stream == null)
        {
            throw new InvalidOperationException(
                $"Embedded database resource not found: {embeddedResourceName}. " +
                $"Ensure the file is marked as EmbeddedResource in .csproj");
        }
        
        using FileStream fileStream = File.Create(targetPath);
        stream.CopyTo(fileStream);
        
        logger.LogInformation($"Database extracted from embedded resource: {targetPath}");
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
