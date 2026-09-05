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

        Dictionary<string, Type> registry = DiscoverTools(out List<string> registrationErrors);

        // Printed rather than thrown: none of what it reports stops a deployment, so the operator is
        // told at startup instead of discovering it on the first conversation that lacks a tool.
        ReportRegistry(registry, registrationErrors);

        return registry;
    }

    /// <summary>
    /// Finds every <see cref="MorganaTool"/> that declares an intent, keeping the first found per intent.
    /// </summary>
    /// <remarks>
    /// A duplicate is reported rather than resolved: which of two tools reached the scan first depends
    /// on assembly order, so silently keeping one would make the domain's behaviour depend on it.
    /// </remarks>
    /// <param name="registrationErrors">Filled with one message per intent claimed by two tools.</param>
    /// <returns>Intent to tool type, lowercased, case-insensitive.</returns>
    private Dictionary<string, Type> DiscoverTools(out List<string> registrationErrors)
    {
        Dictionary<string, Type> registry = new(StringComparer.OrdinalIgnoreCase);
        registrationErrors = [];

        // Every assembly in the process, since a domain's tools arrive in a plugin DLL that
        // PluginLoaderService has already loaded by the time this runs.
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
                    // An assembly with incomplete dependencies costs only its own types: a half-built
                    // plugin must not hide the tools of every other one.
                    logger.LogWarning("Could not load types from assembly {ArgFullName}: {ExMessage}", a.FullName, ex.Message);
                    return [];
                }
            })
            // Concrete tools that declare which desk they belong to. A tool without the attribute
            // belongs to no agent, so nothing could ever reach it.
            .Where(t => t is { IsClass: true, IsAbstract: false } && t.IsSubclassOf(typeof(MorganaTool)))
            .Where(t => t.GetCustomAttribute<ProvidesToolForIntentAttribute>() != null);

        foreach (Type toolType in toolTypes)
        {
            // Which desk this tool belongs to, in the author's own words. Never absent: a type that does
            // not declare one was filtered out above, so the filter is the guard.
            ProvidesToolForIntentAttribute declaration = toolType.GetCustomAttribute<ProvidesToolForIntentAttribute>()!;

            // Lowercased on the way in, since an intent is typed by hand here and in agents.json.
            string intent = declaration.Intent.ToLowerInvariant();

            // The first tool found keeps the desk. Overwriting would hand the intent to whichever
            // assembly the runtime happened to enumerate last.
            if (registry.TryGetValue(intent, out Type? value))
            {
                string error = $"Duplicate tool registration for intent '{intent}': {value.Name} and {toolType.Name}";
                registrationErrors.Add(error);
                logger.LogError(error);
                continue;
            }

            registry[intent] = toolType;
            Console.WriteLine($"  📦 Registered tool: {toolType.Name} for intent '{declaration.Intent}'");
        }

        Console.WriteLine($"✅ Tool registry initialized with {registry.Count} tool(s)");
        Console.WriteLine();

        return registry;
    }

    /// <summary>
    /// Prints how the discovered tools line up against the discovered agents.
    /// </summary>
    /// <remarks>
    /// Neither mismatch is fatal, which is why this reports instead of throwing: an agent may legally
    /// have no native tool. A tool left behind by a renamed agent is dead code rather than a fault.
    /// </remarks>
    /// <param name="registry">Intent to tool type, as discovered.</param>
    /// <param name="registrationErrors">Intents claimed by two tools, already collected.</param>
    private static void ReportRegistry(IReadOnlyDictionary<string, Type> registry, IReadOnlyList<string> registrationErrors)
    {
        Console.WriteLine("========================================");
        Console.WriteLine("Tool Registry Validation");
        Console.WriteLine("========================================");

        // The agent side of the comparison, taken from the registry service rather than scanned again:
        // two scans that can disagree would report a mismatch neither of them causes.
        Dictionary<string, Type> agentsByIntent = HandlesIntentAgentRegistryService.DiscoverAgents();

        HashSet<string> agentIntents = new(agentsByIntent.Keys, StringComparer.OrdinalIgnoreCase);
        HashSet<string> toolIntents = new(registry.Keys, StringComparer.OrdinalIgnoreCase);

        // The header is owed only if something is actually warned about, so it is written by whichever
        // of the two lists below turns out to be non-empty.
        bool warningsHeaderWritten = false;
        void WriteWarningsHeader()
        {
            if (warningsHeaderWritten)
                return;

            Console.WriteLine();
            Console.WriteLine("Warnings:");
            warningsHeaderWritten = true;
        }

        // An agent with no native tool is legal: an MCP-only agent acquires its competences at runtime,
        // so this says what a reader would otherwise have to guess from silence.
        foreach (string intent in agentIntents.Except(toolIntents, StringComparer.OrdinalIgnoreCase))
        {
            WriteWarningsHeader();
            Console.WriteLine($"  ℹ️  Agent '{intent}' ({agentsByIntent.GetValueOrDefault(intent)?.Name ?? "unknown"}) has no native tool registered!");
        }

        // A tool built for an intent nobody claims: dead code left by a renamed or removed agent far
        // more often than something intended, so it is surfaced without stopping the deployment.
        foreach (string intent in toolIntents.Except(agentIntents, StringComparer.OrdinalIgnoreCase))
        {
            WriteWarningsHeader();
            Console.WriteLine($"  ⚠️  Tool '{registry.GetValueOrDefault(intent)?.Name ?? "unknown"}' provides intent '{intent}' but no agent handles this intent.");
        }

        // The pairs that hold. Printed too, so the absence of a desk from this list is itself readable.
        foreach (string intent in agentIntents.Intersect(toolIntents, StringComparer.OrdinalIgnoreCase))
            Console.WriteLine($"✅ Tool Registry: Agent '{intent}' → Tool '{registry.GetValueOrDefault(intent)?.Name ?? "unknown"}'");

        // Two tools claiming one desk, which unlike the warnings above is a defect somebody must fix:
        // one of the two is unreachable, whichever assembly order decided it.
        if (registrationErrors.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Errors:");
            foreach (string error in registrationErrors)
                Console.WriteLine($"  ❌ {error}");
        }

        Console.WriteLine("========================================");
        Console.WriteLine();
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