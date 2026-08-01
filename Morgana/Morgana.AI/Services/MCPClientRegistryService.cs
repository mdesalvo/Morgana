using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.AI;
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
    /// <summary>
    /// Logger for connection-pool lifecycle and reconnect diagnostics (create, reuse,
    /// session-terminated recovery, disposal). Injected; never null.
    /// </summary>
    private readonly ILogger logger;

    /// <summary>
    /// Connection pool: one MCPClient per pool key, shared across conversations/agents
    /// targeting the same server. ConcurrentDictionary ensures lock-free acquire and
    /// reconnect stay correct: atomic TryAdd/TryRemove guarantee single live client
    /// per key with no double-dispose.
    /// </summary>
    private readonly ConcurrentDictionary<string, MCPClient> mcpClients;

    /// <summary>
    /// Per-pool-key reconnect mutex. Collapses thundering-herd failures (N conversations
    /// sharing one pooled client) into single reconnect: first caller gates and re-establishes;
    /// others observe already-replaced client and adopt it. Keyed by pool key (small static set).
    /// </summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> reconnectGates;

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
        reconnectGates = new ConcurrentDictionary<string, SemaphoreSlim>();
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

    /// <inheritdoc/>
    public async Task<T> ExecuteWithReconnectAsync<T>(
        UsesMCPServerAttribute serverAttribute,
        Func<MCPClient, Task<T>> operation)
    {
        // Invariant that keeps a misspelled/wrong endpoint from being misread as a lost
        // session: GetOrCreateClientAsync only returns a client if McpClient.CreateAsync
        // completed, and CreateAsync performs the initialize handshake eagerly. A bad URI
        // therefore 404s at initialize and throws HERE — outside the catch below — so it
        // surfaces as the existing "Failed to connect" error, never as a spurious retry.
        // Consequently any 404 the catch can observe is on a request that already carries
        // an Mcp-Session-Id: the spec's session-terminated case, not a routing mistake.
        MCPClient mcpClient = await GetOrCreateClientAsync(serverAttribute);
        try
        {
            return await operation(mcpClient);
        }
        catch (Exception ex) when (IsSessionTerminated(ex))
        {
            // Single-flight, instance-conditional recovery (see ReconnectAsync): collapses
            // N concurrent session-terminated failures into one reconnect and refuses to
            // tear down a healthy client another caller already re-established. Retry runs
            // exactly once — a second failure is a real fault (server down, auth, protocol)
            // and is allowed to propagate.
            MCPClient reconnectedMCPClient = await ReconnectAsync(serverAttribute, staleMCPClient: mcpClient);
            return await operation(reconnectedMCPClient);
        }
    }

    /// <inheritdoc/>
    public AIFunction WrapResilientTool(McpClientTool discoveredTool, UsesMCPServerAttribute serverAttribute)
        => new ReconnectingMCPTool(discoveredTool, serverAttribute, this);

    /// <summary>
    /// Recovers from terminated session: holds per-pool-key gate for single-flight reconnect,
    /// replaces pooled client only if still the exact instance the caller saw fail.
    /// Late stragglers adopt already-recovered client instead of evicting, preventing strand/storm.
    /// </summary>
    /// <param name="serverAttribute">The server whose session was terminated.</param>
    /// <param name="staleMCPClient">The exact client instance the caller observed failing.</param>
    /// <returns>A live client for the server — freshly reconnected, or the healthy one a peer already restored.</returns>
    private async Task<MCPClient> ReconnectAsync(UsesMCPServerAttribute serverAttribute, MCPClient staleMCPClient)
    {
        string poolKey = PoolKey(serverAttribute);

        // One binary semaphore (count 1 → mutex) shared by all callers recovering THIS
        // pool key — that is what makes the reconnect single-flight. GetOrAdd's factory
        // may run more than once under first-hit contention, but the dictionary keeps
        // exactly one instance; any redundant SemaphoreSlim is simply never awaited.
        SemaphoreSlim reconnectGate = reconnectGates.GetOrAdd(poolKey, _ => new SemaphoreSlim(1, 1));

        // Serialize the recovery: the first caller proceeds to reconnect; the rest block
        // here and, once released, fall into the instance-conditional check below and
        // adopt the client the winner already published instead of reconnecting again.
        await reconnectGate.WaitAsync();

        try
        {
            // Instance-conditional eviction: if the pooled client is no longer the one we
            // saw die, a peer already healed it while we queued — adopt it, touch nothing.
            if (mcpClients.TryGetValue(poolKey, out MCPClient? currentMCPClient)
                 && !ReferenceEquals(currentMCPClient, staleMCPClient))
            {
                logger.LogDebug("MCP client for {Key} was already reconnected by a peer; reusing it", poolKey);
                return currentMCPClient;
            }

            logger.LogWarning(
                "MCP session terminated for {Key} (HTTP 404 on a session-bearing request — " +
                "the spec-mandated drop signal); reconnecting once (single-flight)", poolKey);

            // We hold the gate and the pooled instance is still the dead one (or absent):
            // evict + dispose it, then connect fresh. The TryRemove is the authoritative
            // single eviction; concurrent catch-callers are serialized behind the gate and
            // will take the early-return branch above once we publish the replacement.
            if (mcpClients.TryRemove(poolKey, out MCPClient? removedMCPClient))
            {
                try
                {
                    await removedMCPClient.DisposeAsync();
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error disposing terminated MCP client: {Key}", poolKey);
                }
            }

            // Mint a fresh session (new initialize handshake). Still under the gate, so
            // no peer recoverer connects in parallel. If this throws (server truly down),
            // it propagates out through the finally below — the gate is released, the key
            // is left empty, and the next caller retries cleanly from a known state.
            MCPClient reconnectedMCPClient = await MCPClient.ConnectAsync(serverAttribute, logger);

            // A first-time GetOrCreateClientAsync (not gated — it serves the cold-acquire
            // path) may have populated the key while we were connecting. Mirror that
            // method's race rule: keep the incumbent, dispose our redundant one. Otherwise
            // publish ours.
            if (mcpClients.TryAdd(poolKey, reconnectedMCPClient))
                return reconnectedMCPClient;

            // Cold-acquire won the race: our just-connected client is surplus. Dispose it
            // (no socket leak) and hand back the incumbent the pool now holds.
            await reconnectedMCPClient.DisposeAsync();
            return mcpClients[poolKey];
        }
        finally
        {
            // Always release — including when ConnectAsync threw — so a failed reconnect
            // never deadlocks the queued recoverers; the next one re-evaluates from the
            // (now empty) pool and attempts its own connect.
            reconnectGate.Release();
        }
    }

    /// <summary>
    /// True when exception (or inner) is MCP's "session terminated, re-initialize" signal:
    /// HTTP 404 NotFound per Streamable HTTP spec. Server-agnostic recovery for any MCP host
    /// whose session store doesn't survive recycling/scale-out. Private: ReconnectingMCPTool
    /// reads it directly as nested class.
    /// </summary>
    private static bool IsSessionTerminated(Exception? ex)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current is HttpRequestException { StatusCode: HttpStatusCode.NotFound })
                return true;
        }

        return false;
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
        List<Task> disconnectTasks = [];
        disconnectTasks.AddRange(
            mcpClients.Select(kvp => Task.Run(async () =>
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
            })));
        await Task.WhenAll(disconnectTasks);
        mcpClients.Clear();

        // Release the per-key reconnect gates: SemaphoreSlim is IDisposable and the
        // registry is being torn down, so nothing will queue on them again.
        foreach (SemaphoreSlim reconnectGate in reconnectGates.Values)
            reconnectGate.Dispose();
        reconnectGates.Clear();

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

    /// <summary>
    /// MCP tool wrapper that survives server session drops mid-conversation. Long-lived agent actor calls through
    /// cached tool; on session-terminated (HTTP 404), reconnects via registry's single-flight gate and retries once.
    /// Fast path zero-cost when session never drops. Only WrapResilientTool constructs; nested for IsSessionTerminated visibility.
    /// </summary>
    private sealed class ReconnectingMCPTool : AIFunction
    {
        private readonly MCPClientRegistryService registry;
        private readonly UsesMCPServerAttribute serverAttribute;
        private readonly string toolName;

        /// <summary>
        /// The tool this instance currently calls through. Read and replaced without a lock: a race
        /// between two concurrent tool calls both observing a dead session is benign — both fall
        /// through to the refresh below, the registry's own single-flight reconnect gate
        /// (<see cref="ReconnectAsync"/>) collapses the redundant work, and whichever refreshed
        /// reference is written last is the one every following call observes.
        /// </summary>
        private McpClientTool currentTool;

        /// <summary>
        /// Wraps a freshly-discovered <see cref="McpClientTool"/>, capturing its schema once and its
        /// invocation as a cache that only refreshes itself on a session-terminated failure.
        /// </summary>
        public ReconnectingMCPTool(McpClientTool discoveredTool, UsesMCPServerAttribute serverAttribute, MCPClientRegistryService registry)
        {
            this.registry = registry;
            this.serverAttribute = serverAttribute;
            toolName = discoveredTool.Name;
            currentTool = discoveredTool;

            Name = discoveredTool.Name;
            Description = discoveredTool.Description;
            JsonSchema = discoveredTool.JsonSchema;
            ReturnJsonSchema = discoveredTool.ReturnJsonSchema;
        }

        /// <inheritdoc/>
        public override string Name { get; }

        /// <inheritdoc/>
        public override string Description { get; }

        /// <inheritdoc/>
        public override JsonElement JsonSchema { get; }

        /// <inheritdoc/>
        public override JsonElement? ReturnJsonSchema { get; }

        /// <summary>
        /// Calls the cached tool directly. Only on the MCP session-terminated signal does it
        /// re-discover through the reconnect-safe path and retry once — any other failure (bad
        /// arguments, a genuine server error) propagates immediately, exactly as it would from a
        /// bare <c>McpClientTool</c>.
        /// </summary>
        protected override async ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            try
            {
                return await currentTool.InvokeAsync(arguments, cancellationToken);
            }
            catch (Exception ex) when (IsSessionTerminated(ex))
            {
                // Falls through to the refresh below — the cached tool is dead, not the request.
            }

            McpClientTool refreshedTool = await registry.ExecuteWithReconnectAsync(
                serverAttribute,
                async client =>
                {
                    IList<McpClientTool> tools = await client.DiscoverToolsAsync(cancellationToken);
                    return tools.FirstOrDefault(tool => string.Equals(tool.Name, toolName, StringComparison.Ordinal))
                        ?? throw new InvalidOperationException($"MCP server '{serverAttribute.Command}' no longer advertises tool '{toolName}'.");
                });

            currentTool = refreshedTool;
            return await refreshedTool.InvokeAsync(arguments, cancellationToken);
        }
    }
}

/// <summary>
/// Wrapper over SDK's McpClient for one connected MCP server (transport + live session). Private constructor; instances
/// created only via ConnectAsync. Owned/pooled by MCPClientRegistryService (per-key, shared across conversations/agents).
/// On session termination (HTTP 404), registry discards and reconnects; no retry here.
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
    /// Selects the transport from the attribute (Http → URI; Stdio → spawned process),
    /// then performs the MCP <c>initialize</c> handshake eagerly via
    /// <see cref="McpClient.CreateAsync"/> — so a bad endpoint fails HERE, at connect, not
    /// later on a tool call. On failure the error is logged and rethrown unchanged; the
    /// caller (<see cref="MCPClientRegistryService.GetOrCreateClientAsync"/>) wraps it as
    /// the user-facing "Failed to connect to MCP server".
    /// </summary>
    /// <param name="attr">Server declaration: transport, command/URI, optional args.</param>
    /// <param name="logger">Logger propagated into the wrapper.</param>
    /// <param name="cancellationToken">Cancels the connect/handshake.</param>
    /// <returns>A connected wrapper with a live session, ready for discovery/invocation.</returns>
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