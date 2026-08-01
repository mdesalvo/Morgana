using System.Reflection;
using Microsoft.Extensions.Logging;
using Morgana.AI.Abstractions;
using Morgana.AI.Attributes;
using Morgana.AI.Interfaces;

namespace Morgana.AI.Services;

/// <summary>
/// Discovers tools via [ProvidesToolForIntent] attribute scanning of all loaded assemblies.
/// Validates tool↔agent mappings; warns on duplicate registrations, orphaned tools, tools-less agents.
/// Console + logger diagnostic output for visibility; builds case-insensitive intent→tool registry.
/// </summary>
public class ProvidesToolForIntentRegistryService : IToolRegistryService
{
    private readonly ILogger logger;

    /// <summary>
    /// Registry mapping intent names to tool types.
    /// Built during service initialization via assembly scanning.
    /// Case-insensitive string comparison for intent matching.
    /// </summary>
    private readonly Lazy<Dictionary<string, Type>> intentToToolType;

    /// <summary>
    /// Initializes a new instance of ProvidesToolForIntentRegistryService.
    /// Performs tool discovery and validation with comprehensive diagnostic output.
    /// </summary>
    /// <param name="logger">Logger instance for diagnostic information</param>
    public ProvidesToolForIntentRegistryService(ILogger logger)
    {
        this.logger = logger;

        intentToToolType = new Lazy<Dictionary<string, Type>>(InitializeRegistry);
    }

    /// <summary>
    /// Scans assemblies for MorganaTool classes with [ProvidesToolForIntent] attribute.
    /// Validates tool↔agent coordination; checks for duplicates/orphans; outputs diagnostics.
    /// </summary>
    /// <returns>Dictionary mapping intent names to tool types (case-insensitive)</returns>
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
                    logger.LogWarning("Could not load types from assembly {ArgFullName}: {ExMessage}", a.FullName, ex.Message);
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

            // Check for duplicate tool registrations
            if (registry.TryGetValue(intent, out Type? value))
            {
                string error = $"Duplicate tool registration for intent '{intent}': {value.Name} and {toolType.Name}";
                registrationErrors.Add(error);
                logger.LogError(error);
                continue;
            }

            registry[intent] = toolType;
            Console.WriteLine($"  📦 Registered tool: {toolType.Name} for intent '{attr.Intent}'");
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

        // Check for agents without tools (warning: limited capabilities)
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

        // Check for orphaned tools (tools without agents - warning)
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

        // Display successful mappings (agents with tools)
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

    /// <summary>
    /// Finds the MorganaTool type that provides native tools for the specified intent.
    /// </summary>
    /// <param name="intent">Intent name to find tool for (e.g., "billing")</param>
    /// <returns>
    /// Type of MorganaTool class decorated with [ProvidesToolForIntent(intent)],
    /// or null if no tool found for this intent.
    /// </returns>
    /// <remarks>
    /// <para><strong>Case-Insensitive Matching:</strong></para>
    /// <para>Intent matching uses case-insensitive comparison (normalized to lowercase during registration).</para>
    /// <para><strong>Null Return:</strong></para>
    /// <para>Returns null for intents without tool implementations rather than throwing.
    /// This allows agents to operate with only framework tools (GetContextVariable, SetContextVariable)
    /// if no domain-specific tool exists.</para>
    /// <para><strong>Usage by MorganaAgentAdapter:</strong></para>
    /// <code>
    /// Type? toolType = toolRegistryService.FindToolTypeForIntent("billing");
    /// if (toolType != null)
    /// {
    ///     // Create tool instance and register methods
    ///     MorganaTool tool = (MorganaTool)Activator.CreateInstance(toolType, ...);
    ///     RegisterToolsInAdapter(toolAdapter, tool, toolDefinitions);
    /// }
    /// else
    /// {
    ///     // Agent has no native tool, only framework tools available
    ///     logger.LogInformation("No native tool found for intent 'billing'");
    /// }
    /// </code>
    /// </remarks>
    public Type? FindToolTypeForIntent(string intent)
    {
        return string.IsNullOrWhiteSpace(intent)
            ? null
            : intentToToolType.Value.GetValueOrDefault(intent.ToLowerInvariant());
    }

    /// <summary>
    /// Gets all registered tool types with their associated intents.
    /// </summary>
    /// <returns>Read-only dictionary mapping intent names to tool types</returns>
    /// <remarks>
    /// <para><strong>Usage Scenarios:</strong></para>
    /// <list type="bullet">
    /// <item>Diagnostics: Display available tools at runtime</item>
    /// <item>Validation: Verify configuration consistency</item>
    /// <item>Testing: Enumerate tools for test coverage</item>
    /// </list>
    /// </remarks>
    public IReadOnlyDictionary<string, Type> GetAllRegisteredTools()
    {
        return intentToToolType.Value;
    }
}