using Microsoft.Extensions.AI;
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
    /// Carries transport type, command/URI and optional arguments.
    /// </param>
    /// <returns>Connected MCPClient instance ready for tool discovery and invocation</returns>
    Task<MCPClient> GetOrCreateClientAsync(UsesMCPServerAttribute serverAttribute);

    /// <summary>
    /// Discovers the tools of the declared server as functions an agent may hold for its whole life.
    /// MCP lets a server end its session at any moment and requires the client to open a new one, so an
    /// implementation must reopen the session when discovery meets an ended one and keep every returned
    /// tool callable across later session ends. A tool call may run again on the new session only when it
    /// provably did not run on the old one: a call that may have taken effect must never be repeated.
    /// </summary>
    /// <param name="serverAttribute">The server declaration carried by the agent class.</param>
    /// <param name="cancellationToken">Abandons discovery; a cancelled call never counts as an ended session.</param>
    /// <returns>The server's tools, under the names and schemas the server declares.</returns>
    Task<IList<AIFunction>> DiscoverResilientToolsAsync(UsesMCPServerAttribute serverAttribute, CancellationToken cancellationToken = default);

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