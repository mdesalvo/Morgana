using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Morgana.AI.Abstractions;
using Morgana.AI.Attributes;
using Morgana.AI.Interfaces;
using Morgana.AI.Providers;

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

            // VALIDATION: Check that all agent-requested MCP servers are available
            IServiceCollection.ValidateAgentMCPServerRequirements(mcpServers);

            return services;
        }

        /// <summary>
        /// Validates that all MCP servers requested by agents (via UsesMCPServersAttribute)
        /// are properly configured and enabled in appsettings.json.
        /// Logs warnings for missing servers.
        /// </summary>
        private static void ValidateAgentMCPServerRequirements(Records.MCPServerConfig[] configuredServers)
        {
            // Get all available MCP server names (enabled only)
            HashSet<string> availableServers = configuredServers
                .Where(s => s.Enabled)
                .Select(s => s.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Find all MorganaAgent types with UsesMCPServersAttribute
            IEnumerable<Type> agentsWithMCPRequirements = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic)
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
                .Where(t => t is { IsClass: true, IsAbstract: false } && 
                           t.IsSubclassOf(typeof(MorganaAgent)) &&
                           t.GetCustomAttribute<UsesMCPServersAttribute>() != null);

            // Validate each agent's MCP requirements
            foreach (Type agentType in agentsWithMCPRequirements)
            {
                UsesMCPServersAttribute mcpAttribute = agentType.GetCustomAttribute<UsesMCPServersAttribute>()!;
                string[] requestedServers = mcpAttribute.ServerNames;

                if (requestedServers.Length == 0)
                    continue;

                // Check for missing servers
                string[] missingServers = requestedServers
                    .Where(requested => !availableServers.Contains(requested))
                    .ToArray();

                if (missingServers.Length > 0)
                {
                    string agentName = agentType.GetCustomAttribute<HandlesIntentAttribute>()?.Intent ?? agentType.Name;
                    
                    // Log error - agent won't function properly without required MCP tools
                    Console.WriteLine(
                        $"⚠️  MCP VALIDATION WARNING: Agent '{agentName}' requires MCP servers that are not configured or enabled:");
                    Console.WriteLine(
                        $"   Missing: {string.Join(", ", missingServers)}");
                    Console.WriteLine(
                        $"   Available: {(availableServers.Any() ? string.Join(", ", availableServers) : "none")}");
                    Console.WriteLine(
                        $"   → Please enable these servers in appsettings.json under LLM:MCPServers");
                    Console.WriteLine();
                }
                else
                {
                    string agentName = agentType.GetCustomAttribute<HandlesIntentAttribute>()?.Intent ?? agentType.Name;
                    Console.WriteLine(
                        $"✓ MCP Validation: Agent '{agentName}' has all required servers: {string.Join(", ", requestedServers)}");
                }
            }
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