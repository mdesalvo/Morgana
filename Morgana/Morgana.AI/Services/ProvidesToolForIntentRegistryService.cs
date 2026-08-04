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
    /// <summary>
    /// Logger for the discovery diagnostics: duplicate registrations, orphaned tools and agents
    /// without one. These are warnings, not failures — the only trace they leave is this log.
    /// </summary>
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
        // Collected rather than thrown immediately (see the "Check for duplicate tool
        // registrations" branch below): unlike HandlesIntentAgentRegistryService's intent↔agent
        // validation, a duplicate tool registration here is reported loudly but does NOT abort
        // startup — the first-seen tool simply keeps winning that intent. Tools are an agent's own
        // capability, not the framework's routing table, so a plugin author shipping a conflicting
        // tool is treated as a configuration warning to fix, not a hard boot failure.
        List<string> registrationErrors = [];

        // Discovery of available tools with their declared intent. Every assembly currently loaded
        // into the process is scanned, not just this one — same rationale as
        // HandlesIntentAgentRegistryService's identical scan: a domain author's plugin DLL is
        // exactly where [ProvidesToolForIntent]-decorated MorganaTool subclasses are expected to
        // live, and it's loaded (by PluginLoaderService) before this service ever runs.
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

            // Duplicate registration: reported (registrationErrors, printed further down) but the
            // FIRST tool found for this intent keeps the slot — `continue` skips the overwrite.
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
        Console.WriteLine("========================================");
        Console.WriteLine("Tool Registry Validation");
        Console.WriteLine("========================================");

        // Same reflection scan HandlesIntentAgentRegistryService runs for its own registry —
        // duplicated here rather than shared, because this service needs the raw agent Type list
        // (to cross-reference against tool intents below), not just the finished intent→type map.
        List<Type> agentTypes =
        [
            .. AppDomain.CurrentDomain.GetAssemblies()
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
                .Where(t => t is { IsClass: true, IsAbstract: false } && t.IsSubclassOf(typeof(MorganaAgent)))
                .Where(t => t.GetCustomAttribute<HandlesIntentAttribute>() != null)
        ];

        HashSet<string> agentIntents = agentTypes
            .Select(t => t.GetCustomAttribute<HandlesIntentAttribute>()?.Intent)
            .Where(i => i != null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase)!;

        HashSet<string> toolIntents = [.. registry.Keys];

        // Warning, not an error: an MCP-only agent (see [UsesMCPServer]) legitimately has no
        // native MorganaTool at all — its tools arrive at runtime from the MCP server instead.
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

        // Orphaned tool: a MorganaTool built for an intent no agent currently claims — dead code
        // (renamed/removed agent) more often than intentional, so flagged but not fatal.
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

    /// <summary>Finds the MorganaTool type registered for an intent, or null (case-insensitive).</summary>
    /// <remarks>
    /// Null is a legitimate, expected outcome — not an error: it means the agent for that intent
    /// has no native tool and runs on framework tools alone (GetContextVariable, etc.) or MCP.
    /// </remarks>
    public Type? FindToolTypeForIntent(string intent)
    {
        return string.IsNullOrWhiteSpace(intent)
            ? null
            : intentToToolType.Value.GetValueOrDefault(intent.ToLowerInvariant());
    }

    /// <summary>All registered tool types keyed by intent — diagnostics/validation/testing enumeration.</summary>
    public IReadOnlyDictionary<string, Type> GetAllRegisteredTools()
    {
        return intentToToolType.Value;
    }
}