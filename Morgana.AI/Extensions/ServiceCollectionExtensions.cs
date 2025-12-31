using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Morgana.AI.Abstractions;
using Morgana.AI.Interfaces;
using Morgana.AI.Providers;
using Morgana.AI.Services;

namespace Morgana.AI.Extensions;

/// <summary>
/// Extension methods for registering MCP protocol services with dependency injection.
/// Provides configuration-driven MCP server registration and provider setup.
/// </summary>
public static class ServiceCollectionExtensions
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Register MCP protocol services with DI container.
        /// Loads MCP server configurations from appsettings and registers IMCPServer implementations.
        /// Validates that all MCP servers required by agents (via UsesMCPServersAttribute) are configured.
        /// </summary>
        /// <param name="configuration">Application configuration</param>
        /// <returns>Service collection for chaining</returns>
        public IServiceCollection AddMCPProtocol(IConfiguration configuration)
        {
            // Load MCP server configurations from appsettings.json
            Records.MCPServerConfig[] mcpServers = configuration
                .GetSection("LLM:MCPServers")
                .Get<Records.MCPServerConfig[]>() ?? [];

            if (mcpServers.Length == 0)
            {
                // No MCP servers configured - skip registration
                return services;
            }
        
            // Register enabled MCP servers as IMCPServer
            foreach (Records.MCPServerConfig serverConfig in mcpServers.Where(s => s.Enabled))
            {
                services.AddSingleton<IMCPServer>(sp =>
                {
                    return serverConfig.Type switch
                    {
                        Records.MCPServerType.InProcess => CreateInProcessServer(serverConfig, sp),
                        Records.MCPServerType.HTTP => throw new NotImplementedException("HTTP MCP client not yet implemented (planned for v0.6+)"),
                        _ => throw new InvalidOperationException($"Unknown MCP server type: {serverConfig.Type}")
                    };
                });
            }
            
            // Register MorganaMCPToolProvider (orchestrator)
            services.AddSingleton<IMCPToolProvider>(sp =>
            {
                ILogger<MorganaMCPToolProvider> logger = sp.GetRequiredService<ILogger<MorganaMCPToolProvider>>();
                IEnumerable<IMCPServer> servers = sp.GetServices<IMCPServer>();
                return new MorganaMCPToolProvider(servers, logger);
            });

            // Register MCP Server Registry Service (with validation)
            services.AddSingleton<IMCPServerRegistryService>(sp => new UsesMCPServersRegistryService(mcpServers));

            return services;
        }
    }
    
    /// <summary>
    /// Create in-process MCP server instance via reflection.
    /// Discovers server implementations from all loaded assemblies by scanning for MorganaMCPServer derivatives.
    /// </summary>
    /// <param name="config">Server configuration</param>
    /// <param name="serviceProvider">Service provider for DI</param>
    /// <returns>Initialized IMCPServer instance</returns>
    private static IMCPServer CreateInProcessServer(
        Records.MCPServerConfig config,
        IServiceProvider serviceProvider)
    {
        // Find all types that inherit from MorganaMCPServer across all loaded assemblies
        Type? mcpServerType = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic) // Skip dynamic assemblies
            .SelectMany(a =>
            {
                try
                {
                    return a.GetTypes();
                }
                catch (ReflectionTypeLoadException)
                {
                    return [];
                }
            })
            .Where(t => t is { IsClass: true, IsAbstract: false } && t.IsSubclassOf(typeof(MorganaMCPServer)))
            .FirstOrDefault(t => t.Name.Equals($"{config.Name}MCPServer", StringComparison.OrdinalIgnoreCase));

        if (mcpServerType == null)
        {
            // Fallback: List all available MCP servers for debugging
            List<string> availableServers = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic)
                .SelectMany(a =>
                {
                    try { return a.GetTypes(); }
                    catch { return []; }
                })
                .Where(t => t is { IsClass: true, IsAbstract: false } && t.IsSubclassOf(typeof(MorganaMCPServer)))
                .Select(t => t.Name)
                .ToList();

            string availableList = availableServers.Any() 
                ? string.Join(", ", availableServers) 
                : "None found";

            throw new InvalidOperationException(
                $"MCP server implementation not found for '{config.Name}'. " +
                $"Expected class name: '{config.Name}MCPServer'. " +
                $"Available MCP servers: {availableList}");
        }

        // Instantiate with DI (constructor injection)
        // ActivatorUtilities will resolve: ILogger<T>, IConfiguration from DI
        // We pass only: config (non-DI parameter)
        return (IMCPServer)ActivatorUtilities.CreateInstance(
            serviceProvider,
            mcpServerType,
            config);
    }
}