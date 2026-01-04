using System.Reflection;
using Microsoft.Extensions.Logging;
using Morgana.AI.Abstractions;
using Morgana.AI.Attributes;
using Morgana.AI.Interfaces;
using Morgana.AI.Tools;

namespace Morgana.AI.Services;

/// <summary>
/// Implementation of IToolRegistryService that discovers tools via reflection.
/// Scans all loaded assemblies for MorganaTool classes marked with ProvidesToolForIntentAttribute.
/// </summary>
public class ProvidesToolForIntentRegistryService : IToolRegistryService
{
    private readonly ILogger logger;
    private readonly Dictionary<string, Type> intentToToolType;
    
    public ProvidesToolForIntentRegistryService(ILogger logger)
    {
        this.logger = logger;
        
        intentToToolType = InitializeRegistry();
    }

    private Dictionary<string, Type> InitializeRegistry()
    {
        Console.WriteLine("🔍 Scanning assemblies for MorganaTool implementations...");

        Dictionary<string, Type> registry = new(StringComparer.OrdinalIgnoreCase);
        List<string> registrationErrors = [];

        // Discovery of available tools with their declared intent
        // Scan ALL loaded assemblies, not just executing assembly
        IEnumerable<Type> toolTypes = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic)
            .SelectMany(a =>
            {
                try
                {
                    return a.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    logger.LogWarning($"Could not load types from assembly {a.FullName}: {ex.Message}");
                    return [];
                }
            })
            .Where(t => t is { IsClass: true, IsAbstract: false } && t.IsSubclassOf(typeof(MorganaTool)))
            .Where(t => t.GetCustomAttribute<ProvidesToolForIntentAttribute>() != null);
        foreach (Type toolType in toolTypes)
        {
            ProvidesToolForIntentAttribute? attr = toolType.GetCustomAttribute<ProvidesToolForIntentAttribute>();
            if (attr == null)
                continue;

            string intent = attr.Intent.ToLowerInvariant();
            if (registry.TryGetValue(intent, out Type? value))
            {
                string error = $"Duplicate tool registration for intent '{intent}': {value.Name} and {toolType.Name}";
                registrationErrors.Add(error);
                logger.LogError(error);
                continue;
            }
            
            registry[intent] = toolType;
            Console.WriteLine($"  📝 Registered tool: {toolType.Name} for intent '{attr.Intent}'");
        }
        
        Console.WriteLine($"✅ Tool registry initialized with {registry.Count} tool(s)");
        Console.WriteLine();

        #region Validation
        // Bidirectional validation of tools and agents
        Console.WriteLine("========================================");
        Console.WriteLine("Tool Registry Validation");
        Console.WriteLine("========================================");

        // Find all agent types with HandlesIntentAttribute
        List<Type> agentTypes = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic)
            .SelectMany(a =>
            {
                try { return a.GetTypes(); }
                catch (ReflectionTypeLoadException) { return []; }
            })
            .Where(t => t is { IsClass: true, IsAbstract: false } && t.IsSubclassOf(typeof(MorganaAgent)))
            .Where(t => t.GetCustomAttribute<HandlesIntentAttribute>() != null)
            .ToList();

        HashSet<string> agentIntents = agentTypes
            .Select(t => t.GetCustomAttribute<HandlesIntentAttribute>()?.Intent)
            .Where(i => i != null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase)!;

        HashSet<string> toolIntents = [.. registry.Keys];

        // Check for agents without tools
        List<string> agentsWithoutTools = [.. agentIntents.Except(toolIntents, StringComparer.OrdinalIgnoreCase)];
        if (agentsWithoutTools.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Warnings:");
            foreach (string intent in agentsWithoutTools)
            {
                Type? agentType = agentTypes.FirstOrDefault(t => 
                    string.Equals(t.GetCustomAttribute<HandlesIntentAttribute>()?.Intent, intent, StringComparison.OrdinalIgnoreCase));
                
                string message = $"ℹ️  Agent '{intent}' ({agentType?.Name ?? "unknown"}) has no native tool registered!";
                Console.WriteLine($"  {message}");
            }
        }

        // Check for orphaned tools (tools without agents)
        List<string> toolsWithoutAgents = [.. toolIntents.Except(agentIntents, StringComparer.OrdinalIgnoreCase)];
        if (toolsWithoutAgents.Count > 0)
        {
            if (agentsWithoutTools.Count == 0)
                Console.WriteLine();
            
            if (agentsWithoutTools.Count == 0)
                Console.WriteLine("Warnings:");
            
            foreach (string intent in toolsWithoutAgents)
            {
                Type? toolType = registry.GetValueOrDefault(intent);
                string message = $"⚠️  Tool '{toolType?.Name ?? "unknown"}' provides intent '{intent}' but no agent handles this intent.";
                Console.WriteLine($"  {message}");
            }
        }

        // Display successful mappings
        foreach (string intent in agentIntents.Intersect(toolIntents, StringComparer.OrdinalIgnoreCase))
        {
            Type? toolType = registry.GetValueOrDefault(intent);
            Console.WriteLine($"✅ Tool Registry: Agent '{intent}' → Tool '{toolType?.Name ?? "unknown"}'");
        }

        // Display duplicate errors if any
        if (registrationErrors.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Errors:");
            foreach (string error in registrationErrors)
            {
                Console.WriteLine($"  ❌ {error}");
            }
        }

        Console.WriteLine("========================================");
        Console.WriteLine();
        #endregion

        return registry;
    }

    public Type? FindToolTypeForIntent(string intent)
    {
        if (string.IsNullOrWhiteSpace(intent))
            return null;
        
        return intentToToolType.GetValueOrDefault(intent.ToLowerInvariant());
    }
    
    public IReadOnlyDictionary<string, Type> GetAllRegisteredTools()
    {
        return intentToToolType;
    }
}