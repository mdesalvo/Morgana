using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Morgana.AI.Attributes;
using Morgana.AI.Interfaces;

namespace Morgana.AI.Services;

/// <summary>
/// Manages MCP client connections: pooling, lazy connect and recovery from an ended session. Pool key
/// comes straight from <see cref="UsesMCPServerAttribute"/> (URI for Http, command path for Stdio) — no
/// external configuration needed, agents are fully self-contained.
/// </summary>
/// <remarks>
/// MCP lets a server end a session at any time and requires the client to open a new one, which the SDK
/// leaves to its caller: an ended client stays ended. So a pooled client whose session has ended is replaced
/// once for all its sharers. Only discovery runs again on the new session: a tool call may already have taken effect.
/// </remarks>
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
    /// One gate per pool key around the replacement of an ended client. Every conversation sharing that
    /// server meets the ended session at about the same moment: the first one replaces the client and
    /// the others adopt the replacement instead of opening a session each.
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

        // What identifies one server across every agent that declares it: two agents naming the same
        // endpoint are asking for the same open session, not for two.
        string poolKey = PoolKey(serverAttribute);

        // A server is connected once per pool key and shared: the handshake is the expensive part,
        // while every agent declaring that same server wants the session already open.
        if (mcpClients.TryGetValue(poolKey, out MCPClient? pooledMCPClient))
        {
            // A session the server has already ended serves nobody: it is replaced before being handed out
            if (pooledMCPClient.IsSessionEnded)
                return await ReconnectAsync(serverAttribute, pooledMCPClient);

            logger.LogDebug("Reusing existing MCP client for: {Key}", poolKey);
            return pooledMCPClient;
        }

        try
        {
            logger.LogInformation("Creating new MCP client for: {Key}", poolKey);

            // Reaches the server for real, handshake included: an unreachable endpoint fails here, while
            // the agent that declared it is still being built rather than mid-conversation.
            MCPClient mcpClient = await MCPClient.ConnectAsync(serverAttribute, logger);

            // Two agents can be built concurrently and both miss the lookup above, so the loser of the
            // atomic add drops the transport it just opened rather than leaking an unpooled session.
            if (mcpClients.TryAdd(poolKey, mcpClient))
            {
                logger.LogInformation("Successfully connected to MCP server: {Key}", poolKey);
                return mcpClient;
            }

            // The session this call opened is closed again: the winner's is the one every agent will
            // share, so keeping a second open would leave a connection nobody can reach.
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
    public async Task<IList<AIFunction>> DiscoverResilientToolsAsync(UsesMCPServerAttribute serverAttribute, CancellationToken cancellationToken = default)
    {
        // Each tool remembers the session it was discovered on, so it can tell that session ending from a failure of its own
        ToolBinding[] toolBindings = await ExecuteWithReconnectAsync(serverAttribute, async mcpClient =>
        {
            IList<McpClientTool> discoveredTools = await mcpClient.DiscoverToolsAsync(cancellationToken);
            return discoveredTools.Select(discoveredTool => new ToolBinding(discoveredTool, mcpClient)).ToArray();
        });

        return [.. toolBindings.Select(toolBinding => new ReconnectingMCPTool(toolBinding, serverAttribute, this))];
    }

    /// <summary>
    /// Runs <paramref name="operation"/> on the pooled client of the declared server. When the operation
    /// fails because that client's session has ended, the client is replaced and the operation retried
    /// once. Any other failure propagates, as does a second one. Reserved for operations without effects
    /// on the server, such as discovery: a call that may already have run there must not run again.
    /// </summary>
    private async Task<T> ExecuteWithReconnectAsync<T>(UsesMCPServerAttribute serverAttribute, Func<MCPClient, Task<T>> operation)
    {
        MCPClient mcpClient = await GetOrCreateClientAsync(serverAttribute);
        try
        {
            return await operation(mcpClient);
        }
        catch (Exception ex) when (mcpClient.IsSessionEnded)
        {
            // Only the first caller meeting the ended session sees the server's 404, the others see their call
            // cancelled: the ended session is the signal, whatever the exception.
            logger.LogWarning(ex, "MCP session ended for {Key}; reconnecting and retrying once", PoolKey(serverAttribute));
            MCPClient reconnectedMCPClient = await ReconnectAsync(serverAttribute, mcpClient);
            return await operation(reconnectedMCPClient);
        }
    }

    /// <summary>
    /// Replaces <paramref name="endedMCPClient"/> in the pool with a client on a new session. Callers queue
    /// behind the pool key's gate and whoever arrives after the replacement adopts it.
    /// </summary>
    private async Task<MCPClient> ReconnectAsync(UsesMCPServerAttribute serverAttribute, MCPClient endedMCPClient)
    {
        string poolKey = PoolKey(serverAttribute);
        SemaphoreSlim reconnectGate = reconnectGates.GetOrAdd(poolKey, _ => new SemaphoreSlim(1, 1));

        await reconnectGate.WaitAsync();
        try
        {
            // A registry being shut down opens no new session, even for a tool still held by a live agent
            ObjectDisposedException.ThrowIf(disposed, this);

            // Another conversation already replaced the ended client while this one waited at the gate
            if (mcpClients.TryGetValue(poolKey, out MCPClient? currentMCPClient)
                && !ReferenceEquals(currentMCPClient, endedMCPClient)
                && !currentMCPClient.IsSessionEnded)
            {
                return currentMCPClient;
            }

            logger.LogWarning("Replacing the MCP client of {Key}: its session has ended", poolKey);

            // Removed only while it is still the ended one, so a live client pooled meanwhile is never thrown away
            if (currentMCPClient is not null && mcpClients.TryRemove(new KeyValuePair<string, MCPClient>(poolKey, currentMCPClient)))
                await currentMCPClient.DisposeAsync();

            // An unreachable server fails here and leaves the key empty: the next caller connects from scratch
            MCPClient reconnectedMCPClient = await MCPClient.ConnectAsync(serverAttribute, logger);
            if (mcpClients.TryAdd(poolKey, reconnectedMCPClient))
                return reconnectedMCPClient;

            // A first connection outside the gate got into the pool meanwhile: that one is shared, this one closed
            await reconnectedMCPClient.DisposeAsync();
            return mcpClients[poolKey];
        }
        finally
        {
            reconnectGate.Release();
        }
    }

    /// <summary>A discovered tool paired with the client whose session it calls through.</summary>
    private sealed record ToolBinding(McpClientTool Tool, MCPClient MCPClient);

    /// <summary>
    /// An MCP tool an agent can hold for its whole conversation. The agent discovers its tools once, when it
    /// is created, so a bare tool would stay bound to that session. This one moves to the live session of its
    /// server when its own has ended and runs the call there, once.
    /// </summary>
    private sealed class ReconnectingMCPTool : AIFunction
    {
        /// <summary>The pool the tool reconnects through, so every conversation on that server shares one new session.</summary>
        private readonly MCPClientRegistryService registry;

        /// <summary>The server the tool belongs to.</summary>
        private readonly UsesMCPServerAttribute serverAttribute;

        /// <summary>
        /// The tool and session calls currently go through, swapped whole after a reconnect. Two calls racing
        /// over an ended session both refresh it harmlessly: the pool's gate gives them the same new session.
        /// </summary>
        private volatile ToolBinding toolBinding;

        /// <summary>Captures the declaration the model sees from the tool as first discovered.</summary>
        public ReconnectingMCPTool(ToolBinding discoveredToolBinding, UsesMCPServerAttribute serverAttribute, MCPClientRegistryService registry)
        {
            toolBinding = discoveredToolBinding;
            this.serverAttribute = serverAttribute;
            this.registry = registry;

            Name = discoveredToolBinding.Tool.Name;
            Description = discoveredToolBinding.Tool.Description;
            JsonSchema = discoveredToolBinding.Tool.JsonSchema;
            ReturnJsonSchema = discoveredToolBinding.Tool.ReturnJsonSchema;
            JsonSerializerOptions = discoveredToolBinding.Tool.JsonSerializerOptions;
        }

        /// <inheritdoc/>
        public override string Name { get; }

        /// <inheritdoc/>
        public override string Description { get; }

        /// <inheritdoc/>
        public override JsonElement JsonSchema { get; }

        /// <inheritdoc/>
        public override JsonElement? ReturnJsonSchema { get; }

        /// <inheritdoc/>
        public override JsonSerializerOptions JsonSerializerOptions { get; }

        /// <summary>
        /// Calls the server through a live session: a session the library reports as ended is replaced before
        /// the call is sent. A call that fails is never sent again, since it may already have taken effect on
        /// the server. It propagates as it would from the bare tool and the next call finds the new session.
        /// </summary>
        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            ToolBinding liveToolBinding = toolBinding.MCPClient.IsSessionEnded
                ? await RebindToLiveSessionAsync(cancellationToken)
                : toolBinding;

            return await liveToolBinding.Tool.InvokeAsync(arguments, cancellationToken);
        }

        /// <summary>
        /// Moves the tool onto the live session of its server. The new session hands out new tool instances,
        /// matched back by the name the model called, which MCP makes unique within a server.
        /// </summary>
        private async Task<ToolBinding> RebindToLiveSessionAsync(CancellationToken cancellationToken)
        {
            toolBinding = await registry.ExecuteWithReconnectAsync(serverAttribute, async mcpClient =>
            {
                IList<McpClientTool> discoveredTools = await mcpClient.DiscoverToolsAsync(cancellationToken);
                McpClientTool discoveredTool = discoveredTools.FirstOrDefault(tool => string.Equals(tool.Name, Name, StringComparison.Ordinal))
                    ?? throw new InvalidOperationException($"MCP server '{serverAttribute.Command}' no longer advertises tool '{Name}'.");
                return new ToolBinding(discoveredTool, mcpClient);
            });
            return toolBinding;
        }
    }

    /// <summary>Disconnects and removes a specific MCP client from pool.</summary>
    public async Task DisconnectClientAsync(UsesMCPServerAttribute serverAttribute)
    {
        // The same identity the connection was pooled under: this disconnects a server, never one
        // agent's use of it.
        string poolKey = PoolKey(serverAttribute);

        // Taken out of the pool before it is closed, so exactly one caller ever closes it. A server that
        // was never connected is not an error: nothing was holding it open.
        if (mcpClients.TryRemove(poolKey, out MCPClient? disconnectedMCPClient))
        {
            try
            {
                // Closes the session for every agent that was sharing it, which is why nothing here
                // consults who else declared this server.
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
        await Task.WhenAll(mcpClients.Select(kvp => DisconnectOneAsync(kvp.Key, kvp.Value)));

        // Emptied only after every session is closed, so a caller arriving now opens a new connection
        // instead of receiving one already being torn down.
        mcpClients.Clear();

        logger.LogInformation("All MCP clients disconnected");
    }

    /// <summary>Disposes a single pooled client, swallowing and logging any failure.</summary>
    private async Task DisconnectOneAsync(string key, MCPClient client)
    {
        try
        {
            // One server's shutdown, awaited on its own: a stdio server that hangs on exit costs its own
            // line in the log rather than the shutdown of every other one.
            await client.DisposeAsync();
            logger.LogInformation("Disconnected MCP client: {Key}", key);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error disconnecting MCP client: {Key}", key);
        }
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
    /// True once the session behind this client is over — ended by the server, lost with the network or the
    /// process, or disposed. Such a client never recovers, while a call merely cancelled by its caller leaves
    /// the session open.
    /// </summary>
    public bool IsSessionEnded => mcpClient.Completion.IsCompleted;

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
        // How this server is reached, decided but not yet opened. The label travels with it as the
        // name this server answers under in every log line about it.
        IClientTransport transport;
        string label;

        // Two ways a domain author can declare a server, so two ways to reach one. Nothing after this
        // switch knows which was chosen.
        switch (attr.Transport)
        {
            case Records.MCPTransport.Http:
            {
                // A remote server already running somewhere: the address is its whole identity.
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
                // A server this process starts and speaks to over its pipes. Prefixed so a command named
                // like a URL cannot collide with an HTTP server in the pool.
                label = $"stdio:{attr.Command}";
                logger.LogInformation("Connecting to stdio MCP server: {AttrCommand}", attr.Command);

                StdioClientTransportOptions options = new StdioClientTransportOptions
                {
                    Command   = attr.Command,

                    // An empty argument list is handed over as nothing at all: what a declaration omits
                    // must not reach the process as an empty argument it then has to interpret.
                    Arguments = attr.Args.Length > 0 ? attr.Args : null,
                    Name      = label
                };

                transport = new StdioClientTransport(options);
                logger.LogDebug("Created stdio transport: {AttrCommand} {Join}", attr.Command, string.Join(" ", attr.Args));
                break;
            }

            // A transport this build does not know how to reach. Nothing can be connected, so nothing
            // is attempted.
            default:
                throw new NotSupportedException(
                    $"Unsupported MCPTransport value '{attr.Transport}'.");
        }

        try
        {
            // The one moment the server is actually reached: the handshake runs here, so a bad endpoint
            // or a command that will not start is discovered now instead of on the first tool call.
            McpClient mcpClient = await McpClient.CreateAsync(
                transport,
                clientOptions: null,
                cancellationToken: cancellationToken);

            logger.LogInformation("Connected to MCP server: {Label}", label);

            // A live session, ready to be shared by every agent that declared this same server.
            return new MCPClient(mcpClient, label, logger);
        }
        catch (Exception ex)
        {
            // Logged here where the server has a name, then rethrown: the pool above turns it into the
            // failure of the agent that declared it.
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

            // Asked of the server on every agent creation, never cached: what a server offers is its own
            // to change. An agent built now must see what it offers now.
            IList<McpClientTool> tools = await mcpClient.ListToolsAsync(cancellationToken: cancellationToken);

            logger.LogInformation("Discovered {ToolsCount} tools from: {ServerLabel}", tools.Count, ServerLabel);

            // Handed back as the server described them, schemas and prose included: their author is
            // whoever wrote that server, so Morgana adapts none of it.
            return tools;
        }
        catch (Exception ex)
        {
            // An agent whose tools cannot be listed has no competences at all, so this is not survivable
            // the way one unreachable colleague is.
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
            // interface and null is a valid sentinel meaning "no arguments".
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