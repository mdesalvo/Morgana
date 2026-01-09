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
    private readonly McpClient _client;
    private readonly ILogger _logger;
    private readonly MCPServerConfig _config;

    private MCPClient(McpClient client, MCPServerConfig config, ILogger logger)
    {
        _client = client;
        _config = config;
        _logger = logger;
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

        if (uri.Scheme == "stdio")
        {
            // stdio transport
            string command = uri.LocalPath;
            IList<string>? args = null;

            if (config.AdditionalSettings?.TryGetValue("Args", out string? argsJson) == true)
            {
                args = System.Text.Json.JsonSerializer.Deserialize<string[]>(argsJson);
            }

            StdioClientTransportOptions options = new StdioClientTransportOptions
            {
                Command = command,
                Arguments = args,
                Name = config.Name
            };

            transport = new StdioClientTransport(options);
            logger.LogDebug($"Created stdio transport: {command}");
        }
        else if (uri.Scheme == "https" || uri.Scheme == "http")
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
                Dictionary<string, string> headers = new Dictionary<string, string>();
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
        }
        else
        {
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
            _logger.LogDebug($"Discovering tools from: {_config.Name}");
            
            // McpClient.ListToolsAsync returns IList<McpClientTool>
            IList<McpClientTool> mcpTools = await _client.ListToolsAsync(cancellationToken: cancellationToken);
            
            // Extract protocol Tool metadata
            List<Tool> tools = mcpTools.Select(t => t.ProtocolTool).ToList();
            
            _logger.LogInformation($"Discovered {tools.Count} tools from: {_config.Name}");
            return tools;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to discover tools from: {_config.Name}");
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
            _logger.LogDebug($"Calling tool '{toolName}' on: {_config.Name}");
            
            // McpClient.CallToolAsync accepts IReadOnlyDictionary<string, object?>
            CallToolResult result = await _client.CallToolAsync(
                toolName,
                arguments as IReadOnlyDictionary<string, object?>,
                cancellationToken: cancellationToken);

            _logger.LogDebug($"Tool '{toolName}' executed successfully");
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to call tool '{toolName}' on: {_config.Name}");
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
            _logger.LogInformation($"Disconnecting from: {_config.Name}");
            await _client.DisposeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error disconnecting from: {_config.Name}");
        }
    }
}