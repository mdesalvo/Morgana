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
    private readonly Lazy<Dictionary<string, Type>> intentToToolType;
    private readonly List<string> registrationErrors = [];
    
    public ProvidesToolForIntentRegistryService(ILogger logger)
    {
        this.logger = logger;
        
        // Lazy initialization - scan assemblies only once
        intentToToolType = new Lazy<Dictionary<string, Type>>(ScanAssembliesForTools);
    }
    
    public Type? FindToolTypeForIntent(string intent)
    {
        if (string.IsNullOrWhiteSpace(intent))
            return null;
        
        // Case-insensitive lookup
        return intentToToolType.Value.GetValueOrDefault(intent.ToLowerInvariant());
    }
    
    public IReadOnlyDictionary<string, Type> GetAllRegisteredTools()
    {
        return intentToToolType.Value;
    }
    
    public Records.ToolRegistryValidationResult ValidateAgentToolMapping()
    {
        List<string> warnings = [];
        
        // Errors from registration (duplicates)
        List<string> errors = new(registrationErrors);
        
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
        
        foreach (Type agentType in agentTypes)
        {
            HandlesIntentAttribute? intentAttr = agentType.GetCustomAttribute<HandlesIntentAttribute>();
            if (intentAttr == null)
                continue;
            
            string intent = intentAttr.Intent;
            Type? toolType = FindToolTypeForIntent(intent);
            
            if (toolType == null)
            {
                // Agent without native tool - this is OK if it uses only MCP tools
                string warning = $"ℹ️  Agent '{intent}' ({agentType.Name}) has no native tool registered. " +
                               $"This agent may rely exclusively on MCP tools.";
                warnings.Add(warning);
            }
            else
            {
                // Agent has native tool - success (already logged in Console during scan)
                string message = $"✅ Tool Registry: Agent '{intent}' → Tool '{toolType.Name}'";
                Console.WriteLine(message);
            }
        }
        
        // Check for orphaned tools (tools without corresponding agents)
        foreach (KeyValuePair<string, Type> kvp in intentToToolType.Value)
        {
            string toolIntent = kvp.Key;
            Type toolType = kvp.Value;
            
            bool hasAgent = agentTypes.Any(a => 
            {
                HandlesIntentAttribute? attr = a.GetCustomAttribute<HandlesIntentAttribute>();
                return attr != null && attr.Intent.Equals(toolIntent, StringComparison.OrdinalIgnoreCase);
            });
            
            if (!hasAgent)
            {
                string warning = $"⚠️  Tool '{toolType.Name}' provides intent '{toolIntent}' but no agent handles this intent. " +
                               $"This tool will never be used.";
                warnings.Add(warning);
            }
        }
        
        bool isValid = errors.Count == 0;
        
        return new Records.ToolRegistryValidationResult(isValid, warnings, errors);
    }
    
    private Dictionary<string, Type> ScanAssembliesForTools()
    {
        Console.WriteLine("🔍 Scanning assemblies for MorganaTool implementations...");
        
        Dictionary<string, Type> registry = new(StringComparer.OrdinalIgnoreCase);
        
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
            
            if (registry.ContainsKey(intent))
            {
                string error = $"Duplicate tool registration for intent '{intent}': " +
                              $"{registry[intent].Name} and {toolType.Name}";
                registrationErrors.Add(error);
                logger.LogError(error);
                Console.WriteLine($"  ❌ {error}");
                continue;
            }
            
            registry[intent] = toolType;
            Console.WriteLine($"  📝 Registered tool: {toolType.Name} for intent '{attr.Intent}'");
        }
        
        Console.WriteLine($"✅ Tool registry initialized with {registry.Count} tool(s)");
        Console.WriteLine();
        
        return registry;
    }
}