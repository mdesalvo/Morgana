using System.Collections.Concurrent;
using System.Net;
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
    /// The connection pool: one connected <see cref="MCPClient"/> per pool key
    /// (see <see cref="PoolKey"/>), shared across every conversation/agent that targets
    /// the same MCP server. <see cref="ConcurrentDictionary{TKey,TValue}"/> so the
    /// lock-free cold-acquire race in <see cref="GetOrCreateClientAsync"/> and the gated
    /// reconnect in <see cref="ReconnectAsync"/> stay correct under concurrent callers:
    /// atomic <c>TryAdd</c>/<c>TryRemove</c> guarantee a single live client per key with
    /// no double-dispose and no orphaned (undisposed) instance.
    /// </summary>
    private readonly ConcurrentDictionary<string, MCPClient> mcpClients;

    /// <summary>
    /// Per-pool-key reconnect mutex. Collapses a thundering herd of concurrent
    /// session-terminated failures (N conversations sharing one pooled client) into a
    /// single reconnect: the first caller through the gate re-establishes the session;
    /// the rest, queued behind it, observe the already-replaced client and adopt it
    /// instead of each firing their own redundant <c>initialize</c> handshake. Keyed by
    /// pool key, which is a small static set (one entry per declared MCP server), so the
    /// dictionary is effectively bounded and never needs eviction.
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

    /// <summary>
    /// Recovers from a terminated session for the given server, exactly once across all
    /// concurrent callers that observed it. Holds a per-pool-key gate so the reconnect is
    /// single-flight, and replaces the pooled client only if it is still the very instance
    /// the caller saw fail (reference identity). A caller that queued on the gate while a
    /// peer already reconnected — or a late straggler whose 404 came from an
    /// already-replaced session — adopts the current healthy client instead of evicting
    /// it, which would otherwise strand the conversations using it and trigger a
    /// reconnect storm.
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
    /// True when the exception (or any inner exception) is the MCP-standard "session was
    /// terminated, re-initialize" signal. Per the Streamable HTTP transport spec, a server
    /// that has terminated or expired a session MUST answer any request carrying that
    /// <c>Mcp-Session-Id</c> with HTTP <c>404 Not Found</c>, and the client MUST then start
    /// a fresh session. The SDK only sends session-bearing requests after a successful
    /// <c>initialize</c>, so a 404 surfacing here is exactly that spec-mandated signal —
    /// detected on the HTTP status alone, with no coupling to any server's error string or
    /// implementation-defined JSON-RPC code. Server-agnostic by construction: it recovers
    /// any MCP host whose session store does not survive instance recycling or scale-out.
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

    /// <summary>
    /// Disconnects and removes a specific MCP client from the pool.
    /// </summary>
    public async Task DisconnectClientAsync(UsesMCPServerAttribute serverAttribute)
    {
        string poolKey = PoolKey(serverAttribute);

        // Atomic remove-then-dispose: TryRemove yields the client to exactly one caller,
        // so concurrent disconnects can't double-dispose the same instance, and a key
        // that is absent (never created, or already removed by a peer / the reconnect
        // path) is a benign no-op rather than an error.
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

    /// <summary>
    /// Disconnects all MCP clients and clears the pool.
    /// Called during application shutdown or service disposal.
    /// </summary>
    public async Task DisconnectAllAsync()
    {
        logger.LogInformation("Disconnecting {McpClientsCount} MCP clients...", mcpClients.Count);

        // Disconnect all clients in parallel: each may involve network I/O (FIN/RST handshake
        // for Http, stdin close for Stdio). Sequential teardown would serialize that latency.
        // Failures are caught per-client so one broken connection does not block the others.
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

    /// <summary>
    /// Synchronously disconnects all pooled MCP clients. Idempotent.
    /// </summary>
    /// <remarks>
    /// Sync-over-async via <c>GetAwaiter().GetResult()</c> is intentional: the <c>IDisposable</c>
    /// contract is synchronous and this path is only reached during host shutdown, where blocking
    /// briefly on connection teardown is acceptable.
    /// </remarks>
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
/// Thin wrapper over the ModelContextProtocol SDK's <see cref="McpClient"/> for one
/// connected MCP server (one transport + one live session). It adds nothing to the
/// protocol — it only owns the SDK client, tags every operation/log line with a stable
/// <see cref="ServerLabel"/>, and normalises construction (transport selection) and
/// teardown.
/// </summary>
/// <remarks>
/// <para>Instances are created exclusively through the <see cref="ConnectAsync"/> factory
/// (the constructor is private) and are meant to be owned and pooled by
/// <see cref="MCPClientRegistryService"/> — one wrapper per pool key, shared across every
/// conversation/agent targeting that server. Do not new this up or dispose it directly:
/// the registry manages its lifetime (pool eviction, single-flight reconnect, teardown).</para>
/// <para><strong>Session lifetime:</strong> the SDK session is established once during
/// <see cref="ConnectAsync"/> and used by every subsequent <see cref="DiscoverToolsAsync"/>
/// / <see cref="CallToolAsync"/>. Those are the session-bearing calls that can hit the MCP
/// spec's "session terminated" HTTP 404 if the server drops the session; this wrapper does
/// not retry — recovery is the registry's job via
/// <see cref="MCPClientRegistryService.ExecuteWithReconnectAsync{T}"/>, which discards the
/// dead wrapper and connects a fresh one.</para>
/// </remarks>
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
    public async Task<IList<Tool>> DiscoverToolsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            logger.LogDebug("Discovering tools from: {ServerLabel}", ServerLabel);

            IList<McpClientTool> mcpTools = await mcpClient.ListToolsAsync(cancellationToken: cancellationToken);
            List<Tool> tools = mcpTools.Select(t => t.ProtocolTool).ToList();

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