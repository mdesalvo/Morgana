using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Morgana.Framework.Interfaces;
using System.Collections.Concurrent;
using System.Text.Json;

namespace Morgana.Framework.Services;

/// <summary>
/// Service implementation for managing MCP client connections.
/// Provides connection pooling, lazy initialization, and lifecycle management.
/// </summary>
public class MCPClientRegistryService : IMCPClientRegistryService
{
    private readonly IConfiguration configuration;
    private readonly ILogger logger;

    private readonly ConcurrentDictionary<string, MCPClient> mcpClients;
    private readonly Dictionary<string, Records.MCPServerConfig> serverConfigs;
    private bool disposed;

    public MCPClientRegistryService(IConfiguration configuration, ILogger logger)
    {
        this.configuration = configuration;
        this.logger = logger;
        this.mcpClients = new ConcurrentDictionary<string, MCPClient>();
        this.serverConfigs = new Dictionary<string, Records.MCPServerConfig>();

        LoadServerConfigurations();
    }

    /// <summary>
    /// Loads MCP server configurations from appsettings.json on service initialization.
    /// </summary>
    private void LoadServerConfigurations()
    {
        IConfigurationSection? mcpSection = configuration.GetSection("Morgana:MCPServers");
        if (mcpSection == null || !mcpSection.Exists())
        {
            logger.LogInformation("No MCP servers configured in appsettings.json");
            return;
        }

        List<Records.MCPServerConfig>? configs = mcpSection.Get<List<Records.MCPServerConfig>>();
        if (configs == null || configs.Count == 0)
        {
            logger.LogInformation("MCP servers section exists but contains no configurations");
            return;
        }

        foreach (Records.MCPServerConfig config in configs)
        {
            if (config.Enabled)
            {
                serverConfigs[config.Name] = config;
                logger.LogInformation($"Loaded MCP server config: {config.Name} ({config.Uri})");
            }
            else
            {
                logger.LogInformation($"Skipped disabled MCP server: {config.Name}");
            }
        }

        logger.LogInformation($"Loaded {serverConfigs.Count} enabled MCP server configurations");
    }

    /// <summary>
    /// Gets or creates an MCP client for the specified server.
    /// Thread-safe - uses ConcurrentDictionary.GetOrAdd for atomic get-or-create.
    /// </summary>
    public async Task<MCPClient> GetOrCreateClientAsync(string serverName)
    {
        if (disposed)
            throw new ObjectDisposedException(nameof(MCPClientRegistryService));

        // Check if server config exists
        if (!serverConfigs.TryGetValue(serverName, out Records.MCPServerConfig? config))
        {
            throw new InvalidOperationException(
                $"MCP server '{serverName}' not found in configuration. " +
                $"Available servers: {string.Join(", ", serverConfigs.Keys)}");
        }

        // Check if client already exists
        if (mcpClients.TryGetValue(serverName, out MCPClient? existingClient))
        {
            return existingClient;
        }

        // Create new client (MCPClient.ConnectAsync is static factory method)
        try
        {
            logger.LogInformation($"Creating new MCP client for server: {serverName}");
            MCPClient client = await MCPClient.ConnectAsync(config, logger);

            // Try to add to pool (another thread might have added it meanwhile)
            if (mcpClients.TryAdd(serverName, client))
            {
                logger.LogInformation($"Successfully connected to MCP server: {serverName}");
                return client;
            }

            // Another thread won the race - dispose our client and use theirs
            await client.DisposeAsync();
            return mcpClients[serverName];
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Failed to connect to MCP server: {serverName}");
            throw new InvalidOperationException($"Failed to connect to MCP server '{serverName}'", ex);
        }
    }

    /// <summary>
    /// Disconnects a specific MCP client and removes it from the pool.
    /// </summary>
    public async Task DisconnectClientAsync(string serverName)
    {
        if (mcpClients.TryRemove(serverName, out MCPClient? client))
        {
            try
            {
                await client.DisposeAsync();
                logger.LogInformation($"Disconnected MCP client: {serverName}");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, $"Error disconnecting MCP client: {serverName}");
            }
        }
    }

    /// <summary>
    /// Disconnects all MCP clients and clears the pool.
    /// Called during application shutdown or service disposal.
    /// </summary>
    public async Task DisconnectAllAsync()
    {
        logger.LogInformation($"Disconnecting {mcpClients.Count} MCP clients...");

        List<Task> disconnectTasks = [];
        foreach (KeyValuePair<string, MCPClient> kvp in mcpClients)
        {
            disconnectTasks.Add(Task.Run(async () =>
            {
                try
                {
                    await kvp.Value.DisposeAsync();
                    logger.LogInformation($"Disconnected MCP client: {kvp.Key}");
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, $"Error disconnecting MCP client: {kvp.Key}");
                }
            }));
        }

        await Task.WhenAll(disconnectTasks);
        mcpClients.Clear();
        logger.LogInformation("All MCP clients disconnected");
    }

    // IDisposable / IAsyncDisposable

    public void Dispose()
    {
        if (!disposed)
        {
            DisconnectAllAsync().GetAwaiter().GetResult();
            disposed = true;
        }
    }
    public async ValueTask DisposeAsync()
    {
        if (!disposed)
        {
            await DisconnectAllAsync();
            disposed = true;
        }
    }
}

/// <summary>
/// MCP client using ModelContextProtocol.Core to interact with MCP servers
/// </summary>
public class MCPClient : IAsyncDisposable
{
    private readonly McpClient mcpClient;
    private readonly ILogger logger;
    private readonly Records.MCPServerConfig mcpServerConfig;

    private MCPClient(McpClient mcpClient, Records.MCPServerConfig mcpServerConfig, ILogger logger)
    {
        this.mcpClient = mcpClient;
        this.mcpServerConfig = mcpServerConfig;
        this.logger = logger;
    }

    /// <summary>
    /// Creates and connects to MCP server
    /// </summary>
    public static async Task<MCPClient> ConnectAsync(
        Records.MCPServerConfig config,
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