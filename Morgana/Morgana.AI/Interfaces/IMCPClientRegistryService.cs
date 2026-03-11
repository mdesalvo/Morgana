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