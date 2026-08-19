using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Morgana.AI.Attributes;
using Morgana.AI.Interfaces;

namespace Morgana.AI.Services;

/// <summary>
/// Manages MCP client connections: pooling and lazy connect. Pool key comes straight from
/// <see cref="UsesMCPServerAttribute"/> (URI for Http, command path for Stdio) — no external
/// configuration needed, agents are fully self-contained.
/// </summary>
public class MCPClientRegistryService : IMCPClientRegistryService
{
    /// <summary>
    /// Logger for connection-pool lifecycle diagnostics (create, reuse, disposal). Injected;
    /// never null.
    /// </summary>
    private readonly ILogger logger;

    /// <summary>
    /// Connection pool: one MCPClient per pool key, shared across conversations/agents
    /// targeting the same server. ConcurrentDictionary's atomic TryAdd guarantees a single
    /// live client per key with no double-connect.
    /// </summary>
    private readonly ConcurrentDictionary<string, MCPClient> mcpClients;

    /// <summary>
    /// Latches true after the first <see cref="Dispose"/>/<see cref="DisposeAsync"/>.
    /// Makes teardown idempotent and lets <see cref="GetOrCreateClientAsync"/> fail fast
    /// with <see cref="ObjectDisposedException"/> instead of handing back a client whose
    /// transport is being torn down.
    /// </summary>
    private bool disposed;

    /// <summary>
    /// Initializes the registry with an empty client pool.
    /// </summary>
    /// <param name="logger">Logger for pool diagnostics.</param>
    public MCPClientRegistryService(ILogger logger)
    {
        this.logger = logger;
        mcpClients = new ConcurrentDictionary<string, MCPClient>();
    }

    /// <summary>
    /// Derives a stable pool key from a <see cref="UsesMCPServerAttribute"/>.
    /// Http  → the URI string.
    /// Stdio → "stdio:{command}" (args are intentionally excluded: same executable is expected to be registered once per agent).
    /// </summary>
    private static string PoolKey(UsesMCPServerAttribute attr) =>
        attr.Transport == Records.MCPTransport.Stdio ? $"stdio:{attr.Command}" : attr.Command;

    /// <summary>
    /// Gets an existing MCP client for the given server declaration, or creates and connects a new one.
    /// Thread-safe — uses ConcurrentDictionary to guarantee a single client per pool key.
    /// </summary>
    public async Task<MCPClient> GetOrCreateClientAsync(UsesMCPServerAttribute serverAttribute)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        string poolKey = PoolKey(serverAttribute);

        // Check if client already exists
        if (mcpClients.TryGetValue(poolKey, out MCPClient? pooledMCPClient))
        {
            logger.LogDebug("Reusing existing MCP client for: {Key}", poolKey);
            return pooledMCPClient;
        }

        // Create new client (MCPClient.ConnectAsync is static factory method)
        try
        {
            logger.LogInformation("Creating new MCP client for: {Key}", poolKey);
            MCPClient mcpClient = await MCPClient.ConnectAsync(serverAttribute, logger);

            // TryAdd is atomic — if another thread won the race, dispose ours and use theirs
            if (mcpClients.TryAdd(poolKey, mcpClient))
            {
                logger.LogInformation("Successfully connected to MCP server: {Key}", poolKey);
                return mcpClient;
            }

            // Another thread won the race - dispose our client and use theirs
            await mcpClient.DisposeAsync();
            return mcpClients[poolKey];
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to connect to MCP server: {Key}", poolKey);
            throw new InvalidOperationException($"Failed to connect to MCP server '{poolKey}'", ex);
        }
    }

    /// <summary>Disconnects and removes a specific MCP client from pool.</summary>
    public async Task DisconnectClientAsync(UsesMCPServerAttribute serverAttribute)
    {
        string poolKey = PoolKey(serverAttribute);

        // Atomic TryRemove ensures single caller disposes each client; absent keys are benign no-ops
        if (mcpClients.TryRemove(poolKey, out MCPClient? disconnectedMCPClient))
        {
            try
            {
                await disconnectedMCPClient.DisposeAsync();
                logger.LogInformation("Disconnected MCP client: {Key}", poolKey);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error disconnecting MCP client: {Key}", poolKey);
            }
        }
    }

    /// <summary>Disconnects all MCP clients and clears the pool (idempotent).</summary>
    public async Task DisconnectAllAsync()
    {
        logger.LogInformation("Disconnecting {McpClientsCount} MCP clients...", mcpClients.Count);

        // Disconnect in parallel (network I/O may block); failures caught per-client to avoid cascading
        List<Task> disconnectTasks =
        [
            .. mcpClients.Select(kvp => Task.Run(async () =>
            {
                try
                {
                    await kvp.Value.DisposeAsync();
                    logger.LogInformation("Disconnected MCP client: {KvpKey}", kvp.Key);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error disconnecting MCP client: {KvpKey}", kvp.Key);
                }
            }))
        ];
        await Task.WhenAll(disconnectTasks);
        mcpClients.Clear();

        logger.LogInformation("All MCP clients disconnected");
    }

    // IDisposable / IAsyncDisposable

    /// <summary>Synchronously disconnects all pooled clients via sync-over-async (idempotent).</summary>
    public void Dispose()
    {
        if (!disposed)
        {
            DisconnectAllAsync().GetAwaiter().GetResult();
            disposed = true;
        }
    }

    /// <summary>
    /// Asynchronously disconnects all pooled MCP clients. Idempotent.
    /// </summary>
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
/// Wrapper over SDK's McpClient for one connected MCP server (transport + live session). Private constructor; instances
/// created only via ConnectAsync. Owned/pooled by MCPClientRegistryService (per-key, shared across conversations/agents).
/// </summary>
public class MCPClient : IAsyncDisposable
{
    /// <summary>The underlying SDK client holding the transport and the live MCP session.</summary>
    private readonly McpClient mcpClient;

    /// <summary>Logger for connect / discover / call / disconnect diagnostics, scoped by <see cref="ServerLabel"/>.</summary>
    private readonly ILogger logger;

    /// <summary>
    /// Stable identifier for this server connection (URI for Http, "stdio:{command}" for Stdio).
    /// Matches the pool key used by <see cref="MCPClientRegistryService"/>.
    /// </summary>
    public string ServerLabel { get; }

    /// <summary>
    /// Private: instances come only from <see cref="ConnectAsync"/>, so a wrapper never
    /// exists without an already-connected, session-established SDK client behind it.
    /// </summary>
    private MCPClient(McpClient mcpClient, string serverLabel, ILogger logger)
    {
        this.mcpClient   = mcpClient;
        this.ServerLabel = serverLabel;
        this.logger      = logger;
    }

    /// <summary>
    /// Builds the transport (Http → URI; Stdio → spawned process) then performs the MCP
    /// <c>initialize</c> handshake eagerly, so a bad endpoint fails HERE, not on a later tool call.
    /// </summary>
    public static async Task<MCPClient> ConnectAsync(
        UsesMCPServerAttribute attr,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        // Build the transport (not yet connected); the actual connection + initialize
        // handshake happens once below in McpClient.CreateAsync.
        IClientTransport transport;
        string label;

        switch (attr.Transport)
        {
            case Records.MCPTransport.Http:
            {
                label = attr.Command;
                logger.LogInformation("Connecting to HTTP MCP server: {Label}", label);

                HttpClientTransportOptions options = new HttpClientTransportOptions
                {
                    Endpoint = new Uri(attr.Command),
                    Name     = label
                };

                transport = new HttpClientTransport(options);
                logger.LogDebug("Created HTTP transport: {Label}", label);
                break;
            }

            case Records.MCPTransport.Stdio:
            {
                label = $"stdio:{attr.Command}";
                logger.LogInformation("Connecting to stdio MCP server: {AttrCommand}", attr.Command);

                StdioClientTransportOptions options = new StdioClientTransportOptions
                {
                    Command   = attr.Command,
                    Arguments = attr.Args.Length > 0 ? attr.Args : null,
                    Name      = label
                };

                transport = new StdioClientTransport(options);
                logger.LogDebug("Created stdio transport: {AttrCommand} {Join}", attr.Command, string.Join(" ", attr.Args));
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

            logger.LogInformation("Connected to MCP server: {Label}", label);
            return new MCPClient(mcpClient, label, logger);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to connect to MCP server: {Label}", label);
            throw;
        }
    }

    /// <summary>
    /// Discovers all tools available on the connected MCP server.
    /// </summary>
    public async Task<IList<McpClientTool>> DiscoverToolsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            logger.LogDebug("Discovering tools from: {ServerLabel}", ServerLabel);

            IList<McpClientTool> tools = await mcpClient.ListToolsAsync(cancellationToken: cancellationToken);

            logger.LogInformation("Discovered {ToolsCount} tools from: {ServerLabel}", tools.Count, ServerLabel);
            return tools;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to discover tools from: {ServerLabel}", ServerLabel);
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
            logger.LogDebug("Calling tool '{ToolName}' on: {ServerLabel}", toolName, ServerLabel);

            // The SDK expects IReadOnlyDictionary<string, object?> but callers build a plain
            // Dictionary<string, object>. The 'as' cast is safe: Dictionary implements the
            // interface, and null is a valid sentinel meaning "no arguments".
            CallToolResult result = await mcpClient.CallToolAsync(
                toolName,
                arguments as IReadOnlyDictionary<string, object?>,
                cancellationToken: cancellationToken);

            logger.LogDebug("Tool '{ToolName}' executed successfully", toolName);
            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to call tool '{ToolName}' on: {ServerLabel}", toolName, ServerLabel);
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
            logger.LogInformation("Disconnecting from: {ServerLabel}", ServerLabel);
            await mcpClient.DisposeAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error disconnecting from: {ServerLabel}", ServerLabel);
        }
    }
}