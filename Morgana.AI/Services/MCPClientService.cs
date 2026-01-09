using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Morgana.AI.Adapters;
using Morgana.AI.Interfaces;
using System.Collections.Concurrent;
using static Morgana.AI.Records;

namespace Morgana.AI.Services;

/// <summary>
/// Service implementation for managing MCP client connections.
/// Provides connection pooling, lazy initialization, and lifecycle management.
/// </summary>
public class MCPClientService : IMCPClientService
{
    private readonly IConfiguration configuration;
    private readonly ILogger logger;
    private readonly ConcurrentDictionary<string, MCPClient> clients;
    private readonly Dictionary<string, MCPServerConfig> serverConfigs;
    private bool disposed = false;

    public MCPClientService(IConfiguration configuration, ILogger<MCPClientService> logger)
    {
        this.configuration = configuration;
        this.logger = logger;
        this.clients = new ConcurrentDictionary<string, MCPClient>();
        this.serverConfigs = new Dictionary<string, MCPServerConfig>();

        LoadServerConfigurations();
    }

    /// <summary>
    /// Loads MCP server configurations from appsettings.json on service initialization.
    /// </summary>
    private void LoadServerConfigurations()
    {
        IConfigurationSection? mcpSection = configuration.GetSection("Morgana:MCPServers");
        if (mcpSection == null || !mcpSection.Exists())
        {
            logger.LogInformation("No MCP servers configured in appsettings.json");
            return;
        }

        List<MCPServerConfig>? configs = mcpSection.Get<List<MCPServerConfig>>();
        if (configs == null || configs.Count == 0)
        {
            logger.LogInformation("MCP servers section exists but contains no configurations");
            return;
        }

        foreach (MCPServerConfig config in configs)
        {
            if (config.Enabled)
            {
                serverConfigs[config.Name] = config;
                logger.LogInformation($"Loaded MCP server config: {config.Name} ({config.Uri})");
            }
            else
            {
                logger.LogInformation($"Skipped disabled MCP server: {config.Name}");
            }
        }

        logger.LogInformation($"Loaded {serverConfigs.Count} enabled MCP server configurations");
    }

    /// <summary>
    /// Gets or creates an MCP client for the specified server.
    /// Thread-safe - uses ConcurrentDictionary.GetOrAdd for atomic get-or-create.
    /// </summary>
    public async Task<MCPClient> GetOrCreateClientAsync(string serverName)
    {
        if (disposed)
            throw new ObjectDisposedException(nameof(MCPClientService));

        // Check if server config exists
        if (!serverConfigs.TryGetValue(serverName, out MCPServerConfig? config))
        {
            throw new InvalidOperationException(
                $"MCP server '{serverName}' not found in configuration. " +
                $"Available servers: {string.Join(", ", serverConfigs.Keys)}");
        }

        // Get or create client atomically
        MCPClient client = clients.GetOrAdd(serverName, _ =>
        {
            logger.LogInformation($"Creating new MCP client for server: {serverName}");
            return new MCPClient(config, logger);
        });

        // Ensure client is connected
        if (!client.IsConnected)
        {
            try
            {
                await client.ConnectAsync();
                logger.LogInformation($"Successfully connected to MCP server: {serverName}");
            }
            catch (Exception ex)
            {
                // Remove failed client from pool
                clients.TryRemove(serverName, out _);
                logger.LogError(ex, $"Failed to connect to MCP server: {serverName}");
                throw new InvalidOperationException($"Failed to connect to MCP server '{serverName}'", ex);
            }
        }

        return client;
    }

    /// <summary>
    /// Disconnects a specific MCP client and removes it from the pool.
    /// </summary>
    public async Task DisconnectClientAsync(string serverName)
    {
        if (clients.TryRemove(serverName, out MCPClient? client))
        {
            try
            {
                await client.DisconnectAsync();
                logger.LogInformation($"Disconnected MCP client: {serverName}");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, $"Error disconnecting MCP client: {serverName}");
            }
        }
    }

    /// <summary>
    /// Disconnects all MCP clients and clears the pool.
    /// Called during application shutdown or service disposal.
    /// </summary>
    public async Task DisconnectAllAsync()
    {
        logger.LogInformation($"Disconnecting {clients.Count} MCP clients...");

        List<Task> disconnectTasks = new List<Task>();
        foreach (KeyValuePair<string, MCPClient> kvp in clients)
        {
            disconnectTasks.Add(Task.Run(async () =>
            {
                try
                {
                    await kvp.Value.DisconnectAsync();
                    logger.LogInformation($"Disconnected MCP client: {kvp.Key}");
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, $"Error disconnecting MCP client: {kvp.Key}");
                }
            }));
        }

        await Task.WhenAll(disconnectTasks);
        clients.Clear();
        logger.LogInformation("All MCP clients disconnected");
    }

    public void Dispose()
    {
        if (!disposed)
        {
            DisconnectAllAsync().GetAwaiter().GetResult();
            disposed = true;
        }
    }
}