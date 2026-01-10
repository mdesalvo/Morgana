using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using static Morgana.AI.Records;

namespace Morgana.AI.Adapters;

/// <summary>
/// MCP client wrapper using ModelContextProtocol.Core SDK v0.5.0-preview.1
/// </summary>
public class MCPClient : IAsyncDisposable
{
    private readonly McpClient mcpClient;
    private readonly ILogger logger;
    private readonly MCPServerConfig mcpServerConfig;

    private MCPClient(McpClient mcpClient, MCPServerConfig mcpServerConfig, ILogger logger)
    {
        this.mcpClient = mcpClient;
        this.mcpServerConfig = mcpServerConfig;
        this.logger = logger;
    }

    /// <summary>
    /// Creates and connects to MCP server
    /// </summary>
    public static async Task<MCPClient> ConnectAsync(
        MCPServerConfig config,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation($"Connecting to MCP server: {config.Name} ({config.Uri})");

        Uri uri = new Uri(config.Uri);
        IClientTransport transport;

        switch (uri.Scheme.ToLower())
        {
            case "stdio":
            {
                // stdio transport
                string command = uri.LocalPath;
                IList<string>? args = null;
                if (config.AdditionalSettings?.TryGetValue("Args", out string? argsJson) == true)
                    args = JsonSerializer.Deserialize<string[]>(argsJson);

                StdioClientTransportOptions options = new StdioClientTransportOptions
                {
                    Command = command,
                    Arguments = args,
                    Name = config.Name
                };

                transport = new StdioClientTransport(options);
                logger.LogDebug($"Created stdio transport: {command}");
                break;
            }
            case "https":
            case "http":
            {
                // HTTP transport (SSE or Streamable HTTP)
                HttpClientTransportOptions options = new HttpClientTransportOptions
                {
                    Endpoint = uri,
                    Name = config.Name
                };

                // Add custom headers if present
                if (config.AdditionalSettings != null)
                {
                    Dictionary<string, string> headers = [];
                    foreach (KeyValuePair<string, string> kvp in config.AdditionalSettings)
                    {
                        if (kvp.Key != "Args") // Skip Args, only for stdio
                        {
                            headers[kvp.Key] = kvp.Value;
                        }
                    }
                    if (headers.Count > 0)
                    {
                        options.AdditionalHeaders = headers;
                    }
                }

                transport = new HttpClientTransport(options);
                logger.LogDebug($"Created HTTP transport: {uri}");
                break;
            }
            default:
                throw new NotSupportedException(
                    $"Unsupported scheme: {uri.Scheme}. Use 'stdio://', 'http://', or 'https://'");
        }

        try
        {
            // McpClient.CreateAsync handles connection + initialize handshake
            McpClient mcpClient = await McpClient.CreateAsync(
                transport,
                clientOptions: null, // Use defaults
                cancellationToken: cancellationToken);

            logger.LogInformation($"Connected to MCP server: {config.Name}");
            return new MCPClient(mcpClient, config, logger);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Failed to connect to MCP server: {config.Name}");
            throw;
        }
    }

    /// <summary>
    /// Discovers tools via ListToolsAsync
    /// </summary>
    public async Task<IList<Tool>> DiscoverToolsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            logger.LogDebug($"Discovering tools from: {mcpServerConfig.Name}");
            
            // McpClient.ListToolsAsync returns IList<McpClientTool>
            IList<McpClientTool> mcpTools = await mcpClient.ListToolsAsync(cancellationToken: cancellationToken);
            
            // Extract protocol Tool metadata
            List<Tool> tools = mcpTools.Select(t => t.ProtocolTool).ToList();
            
            logger.LogInformation($"Discovered {tools.Count} tools from: {mcpServerConfig.Name}");
            return tools;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Failed to discover tools from: {mcpServerConfig.Name}");
            throw;
        }
    }

    /// <summary>
    /// Calls a tool via CallToolAsync
    /// </summary>
    public async Task<CallToolResult> CallToolAsync(
        string toolName,
        Dictionary<string, object>? arguments = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            logger.LogDebug($"Calling tool '{toolName}' on: {mcpServerConfig.Name}");
            
            // McpClient.CallToolAsync accepts IReadOnlyDictionary<string, object?>
            CallToolResult result = await mcpClient.CallToolAsync(
                toolName,
                arguments as IReadOnlyDictionary<string, object?>,
                cancellationToken: cancellationToken);

            logger.LogDebug($"Tool '{toolName}' executed successfully");
            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Failed to call tool '{toolName}' on: {mcpServerConfig.Name}");
            throw;
        }
    }

    /// <summary>
    /// Disconnects from MCP server
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            logger.LogInformation($"Disconnecting from: {mcpServerConfig.Name}");
            await mcpClient.DisposeAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Error disconnecting from: {mcpServerConfig.Name}");
        }
    }
}