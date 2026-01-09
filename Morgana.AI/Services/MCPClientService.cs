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
    private readonly ConcurrentDictionary<string, MCPClient> mcpClients;
    private readonly Dictionary<string, MCPServerConfig> serverConfigs;
    private bool disposed = false;

    public MCPClientService(IConfiguration configuration, ILogger logger)
    {
        this.configuration = configuration;
        this.logger = logger;
        this.mcpClients = new ConcurrentDictionary<string, MCPClient>();
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

        // Check if client already exists
        if (mcpClients.TryGetValue(serverName, out MCPClient? existingClient))
        {
            return existingClient;
        }

        // Create new client (MCPClient.ConnectAsync is static factory method)
        try
        {
            logger.LogInformation($"Creating new MCP client for server: {serverName}");
            MCPClient client = await MCPClient.ConnectAsync(config, logger);
            
            // Try to add to pool (another thread might have added it meanwhile)
            if (mcpClients.TryAdd(serverName, client))
            {
                logger.LogInformation($"Successfully connected to MCP server: {serverName}");
                return client;
            }

            // Another thread won the race - dispose our client and use theirs
            await client.DisposeAsync();
            return mcpClients[serverName];
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Failed to connect to MCP server: {serverName}");
            throw new InvalidOperationException($"Failed to connect to MCP server '{serverName}'", ex);
        }
    }

    /// <summary>
    /// Disconnects a specific MCP client and removes it from the pool.
    /// </summary>
    public async Task DisconnectClientAsync(string serverName)
    {
        if (mcpClients.TryRemove(serverName, out MCPClient? client))
        {
            try
            {
                await client.DisposeAsync();
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
        logger.LogInformation($"Disconnecting {mcpClients.Count} MCP clients...");

        List<Task> disconnectTasks = [];
        foreach (KeyValuePair<string, MCPClient> kvp in mcpClients)
        {
            disconnectTasks.Add(Task.Run(async () =>
            {
                try
                {
                    await kvp.Value.DisposeAsync();
                    logger.LogInformation($"Disconnected MCP client: {kvp.Key}");
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, $"Error disconnecting MCP client: {kvp.Key}");
                }
            }));
        }

        await Task.WhenAll(disconnectTasks);
        mcpClients.Clear();
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