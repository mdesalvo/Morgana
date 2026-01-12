using Morgana.Framework.Services;

namespace Morgana.Framework.Interfaces;

/// <summary>
/// Service interface for managing MCP (Model Context Protocol) client connections.
/// Provides connection pooling and lifecycle management for MCP servers.
/// </summary>
public interface IMCPClientRegistryService : IDisposable, IAsyncDisposable
{
    /// <summary>
    /// Gets an existing MCP client or creates a new one if not already connected.
    /// Thread-safe - multiple concurrent calls with same serverName return the same client instance.
    /// </summary>
    /// <param name="serverName">Name of the MCP server from configuration</param>
    /// <returns>Connected MCPClient instance ready for tool discovery and invocation</returns>
    Task<MCPClient> GetOrCreateClientAsync(string serverName);

    /// <summary>
    /// Disconnects and removes a specific MCP client from the pool.
    /// </summary>
    /// <param name="serverName">Name of the MCP server to disconnect</param>
    Task DisconnectClientAsync(string serverName);

    /// <summary>
    /// Disconnects all MCP clients and clears the connection pool.
    /// Called during application shutdown.
    /// </summary>
    Task DisconnectAllAsync();
}