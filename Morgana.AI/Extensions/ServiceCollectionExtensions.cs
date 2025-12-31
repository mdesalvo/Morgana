using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Morgana.AI.Abstractions;
using Morgana.AI.Interfaces;
using Morgana.AI.Providers;

namespace Morgana.AI.Extensions;

/// <summary>
/// Extension methods for registering MCP protocol services with dependency injection.
/// Provides configuration-driven MCP server registration and provider setup.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <param name="services">Service collection</param>
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Register MCP protocol services with DI container.
        /// Loads MCP server configurations from appsettings and registers IMCPServer implementations.
        /// </summary>
        /// <param name="configuration">Application configuration</param>
        /// <returns>Service collection for chaining</returns>
        public IServiceCollection AddMCPProtocol(IConfiguration configuration)
        {
            Console.WriteLine("🔍 AddMCPProtocol called!");

            // Load MCP server configurations from appsettings.json
            Records.MCPServerConfig[] mcpServers = configuration
                .GetSection("Morgana:MCPServers")
                .Get<Records.MCPServerConfig[]>() ?? [];

            Console.WriteLine($"🔍 Found {mcpServers.Length} MCP servers in config");

            if (mcpServers.Length == 0)
            {
                // No MCP servers configured - skip registration
                return services;
            }
        
            // Register enabled MCP servers as IMCPServer
            foreach (Records.MCPServerConfig serverConfig in mcpServers.Where(s => s.Enabled))
            {
                Console.WriteLine($"🔍 Registering IMCPServer...");

                services.AddSingleton<IMCPServer>(sp =>
                {
                    ILogger<MorganaMCPServer> logger = sp.GetRequiredService<ILogger<MorganaMCPServer>>();
                
                    return serverConfig.Type switch
                    {
                        Records.MCPServerType.SQLite => CreateSQLiteServer(serverConfig, logger, sp),
                        Records.MCPServerType.HTTP => throw new NotImplementedException(
                            "HTTP MCP client not yet implemented (planned for v0.6+)"),
                        Records.MCPServerType.InProcess => throw new NotImplementedException(
                            "Custom in-process MCP servers must be registered manually via AddSingleton<IMCPServer>"),
                        _ => throw new InvalidOperationException(
                            $"Unknown MCP server type: {serverConfig.Type}")
                    };
                });

                Console.WriteLine($"🔍 IMCPServer registered!");
            }

            Console.WriteLine($"🔍 Registering IMCPToolProvider...");
            
            // Register MorganaMCPToolProvider (orchestrator)
            services.AddSingleton<IMCPToolProvider, MorganaMCPToolProvider>();

            Console.WriteLine($"🔍 IMCPToolProvider registered!");

            return services;
        }
    }
    
    /// <summary>
    /// Create SQLite-based MCP server instance via reflection.
    /// Discovers server implementations from all loaded assemblies by scanning for MorganaMCPServer derivatives.
    /// </summary>
    /// <param name="config">Server configuration</param>
    /// <param name="logger">Logger instance</param>
    /// <param name="serviceProvider">Service provider for DI</param>
    /// <returns>Initialized IMCPServer instance</returns>
    private static IMCPServer CreateSQLiteServer(
        Records.MCPServerConfig config,
        ILogger<MorganaMCPServer> logger,
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

        logger.LogInformation($"Found MCP server: {mcpServerType.FullName}");

        // Instantiate with DI (constructor injection)
        return (IMCPServer)ActivatorUtilities.CreateInstance(
            serviceProvider,
            mcpServerType,
            config);
    }
}
