using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using static Morgana.AI.Records;

namespace Morgana.AI.Adapters;

/// <summary>
/// MCP client implementation using ModelContextProtocol.Core SDK.
/// Provides connection, tool discovery, and tool invocation for MCP servers.
/// </summary>
public class MCPClient
{
    private readonly MCPServerConfig config;
    private readonly ILogger logger;
    private McpClient? wrappedClient;
    private bool isConnected;

    public bool IsConnected => isConnected;

    public MCPClient(MCPServerConfig config, ILogger logger)
    {
        this.config = config;
        this.logger = logger;
        this.isConnected = false;
    }

    /// <summary>
    /// Establishes connection to the MCP server based on URI scheme.
    /// Supports stdio:// and sse:// (or https://) transports.
    /// </summary>
    public async Task ConnectAsync()
    {
        if (isConnected)
        {
            logger.LogDebug($"MCP client already connected to: {config.Name}");
            return;
        }

        logger.LogInformation($"Connecting to MCP server: {config.Name} ({config.Uri})");

        try
        {
            Uri uri = new Uri(config.Uri);
            IClientTransport transport;

            if (uri.Scheme == "stdio")
            {
                // Create stdio transport
                string command = uri.LocalPath;
                string[]? args = null;

                // Parse args from AdditionalSettings if present
                if (config.AdditionalSettings?.TryGetValue("Args", out string? argsJson) == true)
                {
                    args = JsonSerializer.Deserialize<string[]>(argsJson);
                }

                // Create STDIO transport
                transport = new StdioClientTransport(
                    new StdioClientTransportOptions
                    {
                        Arguments = args, 
                        Command = command
                    });
                logger.LogDebug($"Created stdio transport: {command} {string.Join(" ", args ?? Array.Empty<string>())}");
            }
            else if (uri.Scheme == "sse" || uri.Scheme == "https" || uri.Scheme == "http")
            {
                // Create SSE/HTTP transport
                transport = new HttpClientTransport(
                    new HttpClientTransportOptions
                    {
                        Endpoint = uri
                    });
                logger.LogDebug($"Created SSE transport: {uri}");
            }
            else
            {
                throw new NotSupportedException($"Unsupported MCP transport scheme: {uri.Scheme}");
            }

            // Create client with transport
            wrappedClient = await McpClient.CreateAsync(transport);

            // Initialize connection
            InitializeRequest initRequest = new InitializeRequest
            {
                ProtocolVersion = "1.0",
                ClientInfo = new Implementation
                {
                    Name = "Morgana.AI",
                    Version = "1.0.0"
                },
                Capabilities = new ClientCapabilities
                {
                    // Add client capabilities as needed
                }
            };

            InitializeResponse? initResponse = await wrappedClient.InitializeAsync(initRequest);
            
            if (initResponse == null)
            {
                throw new Exception("MCP server returned null initialize response");
            }

            logger.LogInformation($"Connected to MCP server: {config.Name}, version={initResponse.ProtocolVersion}");
            isConnected = true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Failed to connect to MCP server: {config.Name}");
            isConnected = false;
            throw;
        }
    }

    /// <summary>
    /// Discovers available tools from the MCP server via ListTools request.
    /// </summary>
    /// <returns>List of tool definitions</returns>
    public async Task<List<Tool>> DiscoverToolsAsync()
    {
        if (!isConnected || wrappedClient == null)
        {
            throw new InvalidOperationException($"MCP client not connected to server: {config.Name}");
        }

        try
        {
            logger.LogDebug($"Discovering tools from MCP server: {config.Name}");

            ListToolsResponse? response = await wrappedClient.ListToolsAsync(new ListToolsRequest());

            if (response?.Tools == null)
            {
                logger.LogWarning($"MCP server returned no tools: {config.Name}");
                return new List<Tool>();
            }

            logger.LogInformation($"Discovered {response.Tools.Count} tools from MCP server: {config.Name}");
            return response.Tools;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Failed to discover tools from MCP server: {config.Name}");
            throw;
        }
    }

    /// <summary>
    /// Invokes a tool on the MCP server with the specified arguments.
    /// </summary>
    /// <param name="toolName">Name of the tool to invoke</param>
    /// <param name="arguments">Tool arguments as dictionary</param>
    /// <returns>Tool execution result</returns>
    public async Task<CallToolResult> CallToolAsync(string toolName, Dictionary<string, object> arguments)
    {
        if (!isConnected || wrappedClient == null)
        {
            throw new InvalidOperationException($"MCP client not connected to server: {config.Name}");
        }

        try
        {
            logger.LogDebug($"Calling MCP tool: {toolName} on server: {config.Name}");

            CallToolRequest request = new CallToolRequest
            {
                Name = toolName,
                Arguments = arguments
            };

            CallToolResponse? response = await wrappedClient.CallToolAsync(request);

            if (response?.Result == null)
            {
                throw new Exception($"MCP server returned null result for tool: {toolName}");
            }

            logger.LogDebug($"MCP tool call successful: {toolName}");
            return response.Result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Failed to call MCP tool: {toolName} on server: {config.Name}");
            throw;
        }
    }

    /// <summary>
    /// Disconnects from the MCP server and cleans up resources.
    /// </summary>
    public async Task DisconnectAsync()
    {
        if (!isConnected || wrappedClient == null)
        {
            return;
        }

        try
        {
            logger.LogInformation($"Disconnecting from MCP server: {config.Name}");

            if (wrappedClient is IDisposable disposable)
            {
                disposable.Dispose();
            }

            isConnected = false;
            wrappedClient = null;

            logger.LogInformation($"Disconnected from MCP server: {config.Name}");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Error disconnecting from MCP server: {config.Name}");
            throw;
        }
    }
}