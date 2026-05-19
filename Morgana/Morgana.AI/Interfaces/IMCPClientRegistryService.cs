using Morgana.AI.Attributes;
using Morgana.AI.Services;

namespace Morgana.AI.Interfaces;

/// <summary>
/// Service interface for managing MCP (Model Context Protocol) client connections.
/// Provides connection pooling and lifecycle management for MCP servers.
/// </summary>
public interface IMCPClientRegistryService : IDisposable, IAsyncDisposable
{
    /// <summary>
    /// Gets an existing MCP client for the given server declaration, or creates and connects a new one.
    /// Thread-safe — multiple concurrent calls with the same attribute return the same client instance.
    /// </summary>
    /// <param name="serverAttribute">
    /// The <see cref="UsesMCPServerAttribute"/> declared on the agent class.
    /// Carries transport type, command/URI, and optional arguments.
    /// </param>
    /// <returns>Connected MCPClient instance ready for tool discovery and invocation</returns>
    Task<MCPClient> GetOrCreateClientAsync(UsesMCPServerAttribute serverAttribute);

    /// <summary>
    /// Runs an operation against the pooled client for the given server, transparently
    /// recovering from a terminated server-side session. The MCP Streamable HTTP spec
    /// mandates that a server which has dropped a session answer any request carrying
    /// that session id with HTTP <c>404</c>, and that the client then re-initialize.
    /// When the operation hits that signal, the dead client is evicted, a fresh one is
    /// connected (new <c>initialize</c> handshake → new session), and the operation is
    /// retried once. This makes any MCP host whose session store does not survive
    /// instance recycling or horizontal scale-out usable without manual reconnection.
    /// </summary>
    /// <typeparam name="T">Operation result type.</typeparam>
    /// <param name="serverAttribute">The attribute identifying the target server.</param>
    /// <param name="operation">The work to perform against the (possibly reconnected) client.</param>
    /// <returns>The operation result, after at most one transparent reconnect+retry.</returns>
    Task<T> ExecuteWithReconnectAsync<T>(
        UsesMCPServerAttribute serverAttribute,
        Func<MCPClient, Task<T>> operation);

    /// <summary>
    /// Disconnects and removes a specific MCP client from the pool.
    /// </summary>
    /// <param name="serverAttribute">The attribute identifying the server to disconnect</param>
    Task DisconnectClientAsync(UsesMCPServerAttribute serverAttribute);

    /// <summary>
    /// Disconnects all MCP clients and clears the connection pool.
    /// Called during application shutdown.
    /// </summary>
    Task DisconnectAllAsync();
}