using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Morgana.AI.Attributes;
using Morgana.AI.Interfaces;

namespace Morgana.AI.Services;

/// <summary>
/// Service implementation for managing MCP client connections.
/// Provides connection pooling, lazy initialization, and lifecycle management.
/// </summary>
/// <remarks>
/// <para>
/// Clients are pooled by a key derived from the <see cref="UsesMCPServerAttribute"/>:
/// the URI for Http transport, the command path for Stdio transport.
/// No external configuration is required — agents are fully self-contained.
/// </para>
/// </remarks>
public class MCPClientRegistryService : IMCPClientRegistryService
{
    private readonly ILogger logger;
    private readonly ConcurrentDictionary<string, MCPClient> mcpClients;
    private bool disposed;

    public MCPClientRegistryService(ILogger logger)
    {
        this.logger = logger;
        this.mcpClients = new ConcurrentDictionary<string, MCPClient>();
    }

    /// <summary>
    /// Derives a stable pool key from a <see cref="UsesMCPServerAttribute"/>.
    /// Http  → the URI string.
    /// Stdio → "stdio:{command}" (args are intentionally excluded: same executable
    ///          is expected to be registered once per agent).
    /// </summary>
    private static string PoolKey(UsesMCPServerAttribute attr) =>
        attr.Transport == Records.MCPTransport.Stdio
            ? $"stdio:{attr.Command}"
            : attr.Command;

    /// <summary>
    /// Gets an existing MCP client for the given server declaration, or creates and connects a new one.
    /// Thread-safe — uses ConcurrentDictionary to guarantee a single client per pool key.
    /// </summary>
    public async Task<MCPClient> GetOrCreateClientAsync(UsesMCPServerAttribute serverAttribute)
    {
        if (disposed)
            throw new ObjectDisposedException(nameof(MCPClientRegistryService));

        string key = PoolKey(serverAttribute);

        // Check if client already exists
        if (mcpClients.TryGetValue(key, out MCPClient? existingClient))
        {
            logger.LogDebug($"Reusing existing MCP client for: {key}");
            return existingClient;
        }

        // Create new client (MCPClient.ConnectAsync is static factory method)
        try
        {
            logger.LogInformation($"Creating new MCP client for: {key}");
            MCPClient client = await MCPClient.ConnectAsync(serverAttribute, logger);

            // TryAdd is atomic — if another thread won the race, dispose ours and use theirs
            if (mcpClients.TryAdd(key, client))
            {
                logger.LogInformation($"Successfully connected to MCP server: {key}");
                return client;
            }

            // Another thread won the race - dispose our client and use theirs
            await client.DisposeAsync();
            return mcpClients[key];
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Failed to connect to MCP server: {key}");
            throw new InvalidOperationException($"Failed to connect to MCP server '{key}'", ex);
        }
    }

    /// <summary>
    /// Disconnects and removes a specific MCP client from the pool.
    /// </summary>
    public async Task DisconnectClientAsync(UsesMCPServerAttribute serverAttribute)
    {
        string key = PoolKey(serverAttribute);

        if (mcpClients.TryRemove(key, out MCPClient? client))
        {
            try
            {
                await client.DisposeAsync();
                logger.LogInformation($"Disconnected MCP client: {key}");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, $"Error disconnecting MCP client: {key}");
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
/// MCP client using ModelContextProtocol.Core to interact with a single MCP server.
/// </summary>
public class MCPClient : IAsyncDisposable
{
    private readonly McpClient mcpClient;
    private readonly ILogger logger;
    private readonly string serverLabel;

    private MCPClient(McpClient mcpClient, string serverLabel, ILogger logger)
    {
        this.mcpClient   = mcpClient;
        this.serverLabel = serverLabel;
        this.logger      = logger;
    }

    /// <summary>
    /// Creates and connects to an MCP server described by a <see cref="UsesMCPServerAttribute"/>.
    /// Dispatches to the appropriate transport (Http or Stdio) based on the attribute.
    /// </summary>
    public static async Task<MCPClient> ConnectAsync(
        UsesMCPServerAttribute attr,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        IClientTransport transport;
        string label;

        switch (attr.Transport)
        {
            case Records.MCPTransport.Http:
            {
                label = attr.Command;
                logger.LogInformation($"Connecting to HTTP MCP server: {label}");

                HttpClientTransportOptions options = new HttpClientTransportOptions
                {
                    Endpoint = new Uri(attr.Command),
                    Name     = label
                };

                transport = new HttpClientTransport(options);
                logger.LogDebug($"Created HTTP transport: {label}");
                break;
            }

            case Records.MCPTransport.Stdio:
            {
                label = $"stdio:{attr.Command}";
                logger.LogInformation($"Connecting to stdio MCP server: {attr.Command}");

                StdioClientTransportOptions options = new StdioClientTransportOptions
                {
                    Command   = attr.Command,
                    Arguments = attr.Args.Length > 0 ? attr.Args : null,
                    Name      = label
                };

                transport = new StdioClientTransport(options);
                logger.LogDebug($"Created stdio transport: {attr.Command} {string.Join(" ", attr.Args)}");
                break;
            }

            default:
                throw new NotSupportedException(
                    $"Unsupported MCPTransport value '{attr.Transport}'.");
        }

        try
        {
            McpClient mcpClient = await McpClient.CreateAsync(
                transport,
                clientOptions: null,
                cancellationToken: cancellationToken);

            logger.LogInformation($"Connected to MCP server: {label}");
            return new MCPClient(mcpClient, label, logger);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Failed to connect to MCP server: {label}");
            throw;
        }
    }

    /// <summary>
    /// Discovers all tools available on the connected MCP server.
    /// </summary>
    public async Task<IList<Tool>> DiscoverToolsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            logger.LogDebug($"Discovering tools from: {serverLabel}");

            IList<McpClientTool> mcpTools = await mcpClient.ListToolsAsync(cancellationToken: cancellationToken);
            List<Tool> tools = mcpTools.Select(t => t.ProtocolTool).ToList();

            logger.LogInformation($"Discovered {tools.Count} tools from: {serverLabel}");
            return tools;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Failed to discover tools from: {serverLabel}");
            throw;
        }
    }

    /// <summary>
    /// Invokes a tool on the connected MCP server.
    /// </summary>
    public async Task<CallToolResult> CallToolAsync(
        string toolName,
        Dictionary<string, object>? arguments = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            logger.LogDebug($"Calling tool '{toolName}' on: {serverLabel}");

            CallToolResult result = await mcpClient.CallToolAsync(
                toolName,
                arguments as IReadOnlyDictionary<string, object?>,
                cancellationToken: cancellationToken);

            logger.LogDebug($"Tool '{toolName}' executed successfully");
            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Failed to call tool '{toolName}' on: {serverLabel}");
            throw;
        }
    }

    /// <summary>
    /// Disconnects from the MCP server.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            logger.LogInformation($"Disconnecting from: {serverLabel}");
            await mcpClient.DisposeAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Error disconnecting from: {serverLabel}");
        }
    }
}