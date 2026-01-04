using Microsoft.Extensions.DependencyInjection;
using Morgana.AI.Interfaces;
using Morgana.AI.Services;

namespace Morgana.AI.Extensions;

/// <summary>
/// Extension methods for registering Morgana services with dependency injection.
/// Provides configuration-driven tool registry and validation.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <param name="services">Service collection</param>
    extension(IServiceCollection services)
    {
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
    /// Validates that all agents have proper tool configuration.
    /// Checks for:
    /// - Agents without native tools (silly agents)
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
}