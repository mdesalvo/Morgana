using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Morgana.AI.Abstractions;
using Morgana.AI.Adapters;
using Morgana.AI.Attributes;
using Morgana.AI.Interfaces;
using Morgana.AI.Providers;
using Morgana.AI.Services;

namespace Morgana.AI.Extensions;

/// <summary>
/// Extension methods for registering Morgana services with dependency injection.
/// Provides configuration-driven MCP server registration, tool registry, and validation.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <param name="services">Service collection</param>
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
                        Records.MCPServerType.HTTP => CreateHttpRemoteServer(serverConfig, sp),
                        _ => throw new InvalidOperationException($"Unknown MCP server type: {serverConfig.Type}")
                    };
                });
            }
        
            // Register HttpClient for HTTP MCP servers
            services.AddHttpClient();
        
            // Register MorganaMCPToolProvider (orchestrator)
            services.AddSingleton<IMCPToolProvider>(sp =>
            {
                ILogger<MorganaMCPToolProvider> logger = sp.GetRequiredService<ILogger<MorganaMCPToolProvider>>();
                IEnumerable<IMCPServer> servers = sp.GetServices<IMCPServer>();
                return new MorganaMCPToolProvider(servers, logger);
            });

            // Register the MCP Server registry service
            services.AddSingleton<IMCPServerRegistryService>(new UsesMCPServersRegistryService(mcpServers));
 
            // VALIDATION: Check that all agent-requested MCP servers are available
            ValidateAgentMCPServerRequirements(mcpServers);

            return services;
        }

        /// <summary>
        /// Register tool registry service with DI container.
        /// Scans assemblies for tools marked with ProvidesToolForIntentAttribute.
        /// Validates that all agents have proper tool configuration.
        /// </summary>
        /// <returns>Service collection for chaining</returns>
        public IServiceCollection AddMorganaToolRegistry()
        {
            // Register the tool registry service
            services.AddSingleton<IToolRegistryService, ProvidesToolForIntentRegistryService>();
        
            // VALIDATION: Check that all agent-requested tools are available
            ValidateToolRegistry(services);
        
            return services;
        }
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
                
                // Show which servers are InProcess vs HTTP
                List<string> serverDetails = requestedServers
                    .Select(name =>
                    {
                        Records.MCPServerConfig config = configuredServers.First(s =>
                            s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                        return $"{name} ({config.Type})";
                    })
                    .ToList();
                
                Console.WriteLine(
                    $"✓ MCP Validation: Agent '{agentName}' has all required servers: {string.Join(", ", serverDetails)}");
            }
        }
    }

    /// <summary>
    /// Validates that all agents have proper tool configuration.
    /// Checks for:
    /// - Agents without native tools (may rely on MCP only)
    /// - Tools without corresponding agents (orphaned tools)
    /// - Duplicate tool registrations
    /// </summary>
    private static void ValidateToolRegistry(IServiceCollection services)
    {
        // Build temporary service provider to get the registry service
        using ServiceProvider tempProvider = services.BuildServiceProvider();
        IToolRegistryService? toolRegistry = tempProvider.GetService<IToolRegistryService>();
        
        if (toolRegistry == null)
        {
            Console.WriteLine("⚠️  Tool Registry not available for validation");
            return;
        }
        
        Console.WriteLine();
        Console.WriteLine("========================================");
        Console.WriteLine("Tool Registry Validation");
        Console.WriteLine("========================================");
        
        Records.ToolRegistryValidationResult result = toolRegistry.ValidateAgentToolMapping();
        
        // Display warnings
        if (result.Warnings.Any())
        {
            Console.WriteLine();
            Console.WriteLine("Warnings:");
            foreach (string warning in result.Warnings)
            {
                Console.WriteLine($"  {warning}");
            }
        }
        
        // Display errors (if any)
        if (result.Errors.Any())
        {
            Console.WriteLine();
            Console.WriteLine("Errors:");
            foreach (string error in result.Errors)
            {
                Console.WriteLine($"  ❌ {error}");
            }
        }
        
        Console.WriteLine("========================================");
        Console.WriteLine();
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
    
    /// <summary>
    /// Create HTTP remote MCP server adapter.
    /// First attempts to find a custom HttpMCPServerAdapter derivative (e.g., SecurityCatalogMCPServer with embedded WireMock).
    /// Falls back to standard HttpMCPServerAdapter if no custom implementation found.
    /// </summary>
    /// <param name="config">Server configuration with Endpoint in AdditionalSettings</param>
    /// <param name="serviceProvider">Service provider for DI</param>
    /// <returns>Initialized HttpMCPServerAdapter instance</returns>
    private static IMCPServer CreateHttpRemoteServer(
        Records.MCPServerConfig config,
        IServiceProvider serviceProvider)
    {
        IHttpClientFactory httpClientFactory = serviceProvider.GetRequiredService<IHttpClientFactory>();
        
        // Try to find a custom HttpMCPServerAdapter derivative for this server
        // Example: SecurityCatalogMCPServer extends HttpMCPServerAdapter with embedded WireMock
        Type? customHttpServerType = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic)
            .SelectMany(a =>
            {
                try { return a.GetTypes(); }
                catch (ReflectionTypeLoadException) { return []; }
            })
            .Where(t => t is { IsClass: true, IsAbstract: false } && t.IsSubclassOf(typeof(HttpMCPServerAdapter)))
            .FirstOrDefault(t => t.Name.Equals($"{config.Name}MCPServer", StringComparison.OrdinalIgnoreCase));
        
        if (customHttpServerType != null)
        {
            // Found custom HTTP server implementation (e.g., with embedded WireMock)
            // Use reflection to get correct logger type
            Type loggerType = typeof(ILogger<>).MakeGenericType(customHttpServerType);
            object logger = serviceProvider.GetRequiredService(loggerType);
            
            return (IMCPServer)ActivatorUtilities.CreateInstance(
                serviceProvider,
                customHttpServerType,
                config,
                logger,
                httpClientFactory);
        }
        
        // No custom implementation - use standard HttpMCPServerAdapter (pure HTTP client)
        ILogger<HttpMCPServerAdapter> standardLogger = 
            serviceProvider.GetRequiredService<ILogger<HttpMCPServerAdapter>>();
        
        return new HttpMCPServerAdapter(config, standardLogger, httpClientFactory);
    }
}