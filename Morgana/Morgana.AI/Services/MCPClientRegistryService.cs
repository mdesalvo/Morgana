using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using Morgana.AI.Attributes;
using Morgana.AI.Interfaces;

namespace Morgana.AI.Services;

/// <summary>
/// Pools one MCP session per server that an agent's <see cref="UsesMCPServerAttribute"/> declares, connecting on first use and replacing an ended session.
/// MCP lets a server end a session at any time and the SDK leaves the new one to its caller, so an ended client is replaced once for all its sharers.
/// Only discovery runs again on the new session, since a tool call may already have taken effect.
/// </summary>
public class MCPClientRegistryService : IMCPClientRegistryService
{
    /// <summary>Logs the connections opened, reused, replaced and closed.</summary>
    private readonly ILogger logger;

    /// <summary>The open session of each server by its pool key, shared by every conversation and agent that declares it.</summary>
    private readonly ConcurrentDictionary<string, MCPClient> mcpClients;

    /// <summary>
    /// One gate per pool key around the replacement of an ended client: the first conversation meeting it replaces the client, the others adopt it.
    /// The gates are left out of <see cref="Dispose"/>, since a replacement still inside one at shutdown would fail on leaving it.
    /// </summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> reconnectGates;

    /// <summary>
    /// Whether the registry has been disposed, which makes the teardown idempotent.
    /// A disposed registry refuses a client rather than hand back one whose transport is being torn down.
    /// </summary>
    private bool disposed;

    /// <summary>Starts the registry with no session open: each opens on the first agent that declares its server.</summary>
    public MCPClientRegistryService(ILogger logger)
    {
        this.logger = logger;
        mcpClients = new ConcurrentDictionary<string, MCPClient>();
        reconnectGates = new ConcurrentDictionary<string, SemaphoreSlim>();
    }

    /// <summary>
    /// The identity a server is pooled under and the identity the guard keeps its verdict under:
    /// the URI over HTTP, "stdio:{command}" over stdio, where the arguments are left out because one executable is registered once per agent.
    /// </summary>
    internal static string PoolKey(UsesMCPServerAttribute serverAttribute) =>
        // The prefix keeps a command named like a URL apart from an HTTP server.
        serverAttribute.Transport == Records.MCPTransport.Stdio ? $"stdio:{serverAttribute.Command}" : serverAttribute.Command;

    /// <summary>Returns the pooled session of the declared server, connecting it on first use and replacing it when it has ended.</summary>
    private async Task<MCPClient> GetOrCreateClientAsync(UsesMCPServerAttribute serverAttribute)
    {
        // A registry being shut down opens no session.
        ObjectDisposedException.ThrowIf(disposed, this);

        // What identifies one server across every agent that declares it: two agents naming the same
        // endpoint are asking for the same open session, not for two.
        string poolKey = PoolKey(serverAttribute);

        // A server is connected once per pool key and shared: the handshake is the expensive part,
        // while every agent declaring that same server wants the session already open.
        if (mcpClients.TryGetValue(poolKey, out MCPClient? pooledMCPClient))
        {
            // A session the server has already ended serves nobody: it is replaced before being handed out.
            if (pooledMCPClient.IsSessionEnded)
                return await ReconnectAsync(serverAttribute, pooledMCPClient);

            logger.LogDebug("Reusing existing MCP client for: {Key}", poolKey);

            // The open session, shared.
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

                // This session is the one every agent of that server shares from now on.
                return mcpClient;
            }

            // The session this call opened is closed again: the winner's is the one every agent will
            // share, so keeping a second open would leave a connection nobody can reach.
            await mcpClient.DisposeAsync();
            return mcpClients[poolKey];
        }
        catch (Exception ex)
        {
            // The agent that declared the server fails to build, naming the server it could not reach.
            logger.LogError(ex, "Failed to connect to MCP server: {Key}", poolKey);
            throw new InvalidOperationException($"Failed to connect to MCP server '{poolKey}'", ex);
        }
    }

    /// <inheritdoc/>
    public async Task<IList<AIFunction>> DiscoverResilientToolsAsync(UsesMCPServerAttribute serverAttribute, CancellationToken cancellationToken = default)
    {
        // Each tool remembers the session it was discovered on, so it can tell that session ending from a failure of its own.
        // Discovery has no effect on the server, so it is retried once on a new session.
        ToolBinding[] toolBindings = await ExecuteWithReconnectAsync(serverAttribute, async mcpClient =>
        {
            IList<McpClientTool> discoveredTools = await mcpClient.DiscoverToolsAsync(cancellationToken);
            return discoveredTools.Select(discoveredTool => new ToolBinding(discoveredTool, mcpClient)).ToArray();
        });

        // Tools that an agent can hold for its whole conversation, moving to a new session when theirs ends.
        return [.. toolBindings.Select(toolBinding => new ReconnectingMCPTool(toolBinding, serverAttribute, this))];
    }

    /// <summary>
    /// Runs an operation on the server's pooled session, retrying it once on a new session when that one has ended.
    /// Reserved for operations without effects on the server, such as discovery: a call that may already have run there must not run again.
    /// </summary>
    private async Task<T> ExecuteWithReconnectAsync<T>(UsesMCPServerAttribute serverAttribute, Func<MCPClient, Task<T>> operation)
    {
        // The session the operation runs on first.
        MCPClient mcpClient = await GetOrCreateClientAsync(serverAttribute);
        try
        {
            // Any failure other than an ended session propagates as it is.
            return await operation(mcpClient);
        }
        catch (Exception ex) when (mcpClient.IsSessionEnded)
        {
            // Only the first caller meeting the ended session sees the server's 404, the others see their call
            // cancelled: the ended session is the signal, whatever the exception.
            logger.LogWarning(ex, "MCP session ended for {Key}; reconnecting and retrying once", PoolKey(serverAttribute));

            // One retry on the new session; a second failure propagates.
            MCPClient reconnectedMCPClient = await ReconnectAsync(serverAttribute, mcpClient);
            return await operation(reconnectedMCPClient);
        }
    }

    /// <summary>
    /// Replaces an ended client in the pool with one on a new session.
    /// Callers queue behind the server's gate and whoever arrives after the replacement adopts it.
    /// </summary>
    private async Task<MCPClient> ReconnectAsync(UsesMCPServerAttribute serverAttribute, MCPClient endedMCPClient)
    {
        // One gate per server, so the conversations of one server queue while the others go on.
        string poolKey = PoolKey(serverAttribute);
        SemaphoreSlim reconnectGate = reconnectGates.GetOrAdd(poolKey, _ => new SemaphoreSlim(1, 1));

        // One replacement at a time per server.
        await reconnectGate.WaitAsync();
        try
        {
            // A registry being shut down opens no new session, even for a tool still held by a live agent.
            ObjectDisposedException.ThrowIf(disposed, this);

            // Another conversation already replaced the ended client while this one waited at the gate.
            if (mcpClients.TryGetValue(poolKey, out MCPClient? currentMCPClient)
                && !ReferenceEquals(currentMCPClient, endedMCPClient)
                && !currentMCPClient.IsSessionEnded)
            {
                return currentMCPClient;
            }

            logger.LogWarning("Replacing the MCP client of {Key}: its session has ended", poolKey);

            // Removed only while it is still the ended one, so a live client pooled meanwhile is never thrown away.
            if (currentMCPClient is not null && mcpClients.TryRemove(new KeyValuePair<string, MCPClient>(poolKey, currentMCPClient)))
                await currentMCPClient.DisposeAsync();

            // An unreachable server fails here and leaves the key empty: the next caller connects from scratch.
            MCPClient reconnectedMCPClient = await MCPClient.ConnectAsync(serverAttribute, logger);

            // The new session is shared from now on.
            if (mcpClients.TryAdd(poolKey, reconnectedMCPClient))
                return reconnectedMCPClient;

            // A first connection outside the gate got into the pool meanwhile: that one is shared, this one closed.
            await reconnectedMCPClient.DisposeAsync();
            return mcpClients[poolKey];
        }
        finally
        {
            // The next conversation queued at the gate finds the replacement.
            reconnectGate.Release();
        }
    }

    /// <summary>A discovered tool paired with the client whose session it calls through.</summary>
    private sealed record ToolBinding(McpClientTool Tool, MCPClient MCPClient);

    /// <summary>
    /// An MCP tool that an agent can hold for its whole conversation, moving to its server's live session when its own has ended.
    /// The agent discovers its tools once, when it is created, so a bare tool would stay bound to that first session.
    /// </summary>
    private sealed class ReconnectingMCPTool : AIFunction
    {
        /// <summary>The pool the tool reconnects through, so every conversation on that server shares one new session.</summary>
        private readonly MCPClientRegistryService registry;

        /// <summary>The server the tool belongs to.</summary>
        private readonly UsesMCPServerAttribute serverAttribute;

        /// <summary>
        /// The tool and session that calls currently go through, swapped whole after a reconnect.
        /// Two calls racing over an ended session both refresh it harmlessly: the pool's gate gives them the same new session.
        /// </summary>
        private volatile ToolBinding toolBinding;

        /// <summary>Captures the declaration the model sees from the tool as first discovered.</summary>
        public ReconnectingMCPTool(ToolBinding discoveredToolBinding, UsesMCPServerAttribute serverAttribute, MCPClientRegistryService registry)
        {
            toolBinding = discoveredToolBinding;
            this.serverAttribute = serverAttribute;
            this.registry = registry;

            // The model is shown the tool exactly as the server first described it.
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
        /// Calls the server through a live session, replacing an ended one before the call is sent.
        /// A call that fails is never sent again, since it may already have taken effect on the server.
        /// </summary>
        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            // An ended session is left behind before the call, which is the only moment a move is safe.
            ToolBinding liveToolBinding = toolBinding.MCPClient.IsSessionEnded
                ? await RebindToLiveSessionAsync(cancellationToken)
                : toolBinding;

            // A failure propagates as it would from the bare tool and the next call finds the new session.
            return await liveToolBinding.Tool.InvokeAsync(arguments, cancellationToken);
        }

        /// <summary>
        /// Moves the tool onto the live session of its server.
        /// The new session hands out new tool instances, matched back by the name the model called, which MCP makes unique within a server.
        /// </summary>
        private async Task<ToolBinding> RebindToLiveSessionAsync(CancellationToken cancellationToken)
        {
            // Rediscovery has no effect on the server, so it may retry once more on a session that ended meanwhile.
            toolBinding = await registry.ExecuteWithReconnectAsync(serverAttribute, async mcpClient =>
            {
                IList<McpClientTool> discoveredTools = await mcpClient.DiscoverToolsAsync(cancellationToken);
                McpClientTool discoveredTool = discoveredTools.FirstOrDefault(tool => string.Equals(tool.Name, Name, StringComparison.Ordinal))
                    ?? throw new InvalidOperationException($"MCP server '{serverAttribute.Command}' no longer advertises tool '{Name}'.");
                return new ToolBinding(discoveredTool, mcpClient);
            });

            // The binding every later call of this tool goes through.
            return toolBinding;
        }
    }

    /// <summary>Closes every pooled session and empties the pool.</summary>
    private async Task DisconnectAllAsync()
    {
        logger.LogInformation("Disconnecting {McpClientsCount} MCP clients...", mcpClients.Count);

        // Every session closes at once and each failure stays its own, so one hanging server delays nobody else's close.
        await Task.WhenAll(mcpClients.Select(pooledClient => DisconnectOneAsync(pooledClient.Key, pooledClient.Value)));

        // Emptied only after every session is closed, so a caller arriving now opens a new connection
        // instead of receiving one already being torn down.
        mcpClients.Clear();

        logger.LogInformation("All MCP clients disconnected");
    }

    /// <summary>Closes one pooled session, logging a failure rather than throwing it.</summary>
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
            // A session that fails to close costs only its own line in the log.
            logger.LogError(ex, "Error disconnecting MCP client: {Key}", key);
        }
    }

    /// <summary>Closes every pooled session, once, for a container that disposes synchronously.</summary>
    public void Dispose()
    {
        // A second dispose finds nothing left to close.
        if (!disposed)
        {
            DisconnectAllAsync().GetAwaiter().GetResult();
            disposed = true;
        }

        // Unsealed like every default service, so a derived registry's finalizer would find nothing left to release.
        GC.SuppressFinalize(this);
    }

    /// <summary>Closes every pooled session, once, for a container that disposes asynchronously.</summary>
    public async ValueTask DisposeAsync()
    {
        // A second dispose finds nothing left to close.
        if (!disposed)
        {
            await DisconnectAllAsync();
            disposed = true;
        }

        // Same reason as the synchronous path: the container disposes through whichever one its own shutdown takes.
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// One connected MCP server, its transport and its live session, pooled by <see cref="MCPClientRegistryService"/>.
/// An instance exists only once <see cref="ConnectAsync"/> has completed the handshake.
/// </summary>
public sealed class MCPClient : IAsyncDisposable
{
    /// <summary>The underlying SDK client holding the transport and the live MCP session.</summary>
    private readonly McpClient mcpClient;

    /// <summary>Logs the connection, the discoveries and the disconnection, naming the server by its label.</summary>
    private readonly ILogger logger;

    /// <summary>The name the server answers under in the log: its URI over HTTP, "stdio:{command}" over stdio, as the pool key.</summary>
    private string ServerLabel { get; }

    /// <summary>
    /// Whether the session behind this client is over: ended by the server, lost with the network or the process or disposed.
    /// Such a client never recovers, while a call merely cancelled by its caller leaves the session open.
    /// </summary>
    public bool IsSessionEnded => mcpClient.Completion.IsCompleted;

    /// <summary>Wraps a client whose handshake has completed, which only <see cref="ConnectAsync"/> holds.</summary>
    private MCPClient(McpClient mcpClient, string serverLabel, ILogger logger)
    {
        this.mcpClient = mcpClient;
        ServerLabel = serverLabel;
        this.logger = logger;
    }

    /// <summary>Opens the transport of a declared server and completes the MCP handshake, so a bad endpoint fails here and not on a tool call.</summary>
    public static async Task<MCPClient> ConnectAsync(
        UsesMCPServerAttribute serverAttribute,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        // How this server is reached, decided but not yet opened. The label travels with it as the
        // name this server answers under in every log line about it.
        IClientTransport transport;
        string label;

        // Two ways a domain author can declare a server, so two ways to reach one. Nothing after this
        // switch knows which was chosen.
        switch (serverAttribute.Transport)
        {
            case Records.MCPTransport.Http:
            {
                // A remote server already running somewhere: the address is its whole identity.
                label = serverAttribute.Command;
                logger.LogInformation("Connecting to HTTP MCP server: {Label}", label);

                HttpClientTransportOptions options = new HttpClientTransportOptions
                {
                    Endpoint = new Uri(serverAttribute.Command),
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
                label = $"stdio:{serverAttribute.Command}";
                logger.LogInformation("Connecting to stdio MCP server: {AttrCommand}", serverAttribute.Command);

                StdioClientTransportOptions options = new StdioClientTransportOptions
                {
                    Command   = serverAttribute.Command,

                    // An empty argument list is handed over as nothing at all: what a declaration omits
                    // must not reach the process as an empty argument it then has to interpret.
                    Arguments = serverAttribute.Args.Length > 0 ? serverAttribute.Args : null,
                    Name      = label
                };

                transport = new StdioClientTransport(options);
                logger.LogDebug("Created stdio transport: {AttrCommand} {Join}", serverAttribute.Command, string.Join(" ", serverAttribute.Args));
                break;
            }

            // A transport this build does not know how to reach. Nothing can be connected, so nothing
            // is attempted.
            default:
                throw new NotSupportedException(
                    $"Unsupported MCPTransport value '{serverAttribute.Transport}'.");
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

    /// <summary>Lists the tools the server offers now, as it describes them.</summary>
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

    /// <summary>Closes the session and its transport, logging a failure rather than throwing it.</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            // A stdio server's process ends with its transport.
            logger.LogInformation("Disconnecting from: {ServerLabel}", ServerLabel);
            await mcpClient.DisposeAsync();
        }
        catch (Exception ex)
        {
            // A session that fails to close leaves nothing for the caller to do.
            logger.LogError(ex, "Error disconnecting from: {ServerLabel}", ServerLabel);
        }
    }
}