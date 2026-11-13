using System.Reflection;
using Microsoft.Extensions.Logging;
using Morgana.AI.Tools;
using Morgana.AI.Attributes;
using Morgana.AI.Interfaces;

namespace Morgana.AI.Services;

/// <summary>
/// Finds the tool class of each intent by scanning the loaded assemblies for <c>[ProvidesToolForIntent]</c>.
/// The scan runs at the first lookup and prints how tools and agents line up: an orphan tool and an agent without one are warnings,
/// two tools for one intent stop the startup.
/// </summary>
public class ProvidesToolForIntentRegistryService : IToolRegistryService
{
    /// <summary>
    /// Logs the discovery diagnostics: duplicate registrations and assemblies whose types could not be read.
    /// None of them stops the startup, so this log is the only trace they leave.
    /// </summary>
    private readonly ILogger logger;

    /// <summary>The tool type of each intent, scanned at the first lookup and looked up ignoring case.</summary>
    private readonly Lazy<Dictionary<string, Type>> intentToToolType;

    /// <summary>Defers the scan until the first lookup, when every plugin has been loaded.</summary>
    public ProvidesToolForIntentRegistryService(ILogger logger)
    {
        this.logger = logger;

        // The plugins are loaded before the first lookup, so the scan finds their tools.
        intentToToolType = new Lazy<Dictionary<string, Type>>(InitializeRegistry);
    }

    /// <summary>Scans for the tools and prints how they line up against the agents, refusing two tools for one intent.</summary>
    /// <exception cref="InvalidOperationException">Two tools declare the same intent.</exception>
    private Dictionary<string, Type> InitializeRegistry()
    {
        Console.WriteLine("🔍 Scanning assemblies for MorganaTool implementations...");

        // The tool of each intent and the intents that two tools claimed.
        Records.ToolDiscovery toolDiscovery = DiscoverTools();

        // Printed first, so the operator reads the whole picture even when a duplicate stops the startup below.
        ReportRegistry(toolDiscovery.ToolTypesByIntent, toolDiscovery.RegistrationErrors);

        // Two tools for one intent leave one of them unreachable by assembly order, so the startup stops on it like every other refusal of the topology.
        if (toolDiscovery.RegistrationErrors.Count > 0)
            throw new InvalidOperationException(string.Join(" ", toolDiscovery.RegistrationErrors));

        // The registry that every later lookup reads.
        return toolDiscovery.ToolTypesByIntent;
    }

    /// <summary>
    /// Finds every <see cref="MorganaTool"/> that declares an intent, keeping the first found per intent.
    /// A duplicate is reported rather than resolved: which of two tools reached the scan first depends on assembly order.
    /// </summary>
    private Records.ToolDiscovery DiscoverTools()
    {
        // Intents are typed by hand in two places, so the registry ignores their case.
        Dictionary<string, Type> toolTypesByIntent = new(StringComparer.OrdinalIgnoreCase);
        List<string> registrationErrors = [];

        // Every assembly in the process, since a domain's tools arrive in a plugin DLL that
        // PluginLoaderService has already loaded by the time this runs.
        IEnumerable<Type> toolTypes = AppDomain.CurrentDomain.GetAssemblies()
            .Where(assembly => !assembly.IsDynamic)
            .SelectMany(assembly =>
            {
                try
                {
                    return assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    // An assembly with incomplete dependencies costs only its own types: a half-built
                    // plugin must not hide the tools of every other one.
                    logger.LogWarning("Could not load types from assembly {ArgFullName}: {ExMessage}", assembly.FullName, ex.Message);
                    return [];
                }
            })
            // Concrete tools that declare which agent they belong to. A tool without the attribute
            // belongs to no agent, so nothing could ever reach it.
            .Where(type => type is { IsClass: true, IsAbstract: false } && type.IsSubclassOf(typeof(MorganaTool)))
            .Where(type => type.GetCustomAttribute<ProvidesToolForIntentAttribute>() != null);

        foreach (Type toolType in toolTypes)
        {
            // Which agent this tool belongs to, in the author's own words. Never absent: a type that does
            // not declare one was filtered out above, so the filter is the guard.
            ProvidesToolForIntentAttribute declaration = toolType.GetCustomAttribute<ProvidesToolForIntentAttribute>()!;

            // Lowercased on the way in, since an intent is typed by hand here and in agents.json.
            string intent = declaration.Intent.ToLowerInvariant();

            // The first tool found keeps the agent. Overwriting would hand the intent to whichever
            // assembly the runtime happened to enumerate last.
            if (toolTypesByIntent.TryGetValue(intent, out Type? registeredToolType))
            {
                string error = $"Duplicate tool registration for intent '{intent}': {registeredToolType.Name} and {toolType.Name}";
                registrationErrors.Add(error);
                logger.LogError(error);
                continue;
            }

            // The tool now serves its intent.
            toolTypesByIntent[intent] = toolType;
            Console.WriteLine($"  📦 Registered tool: {toolType.Name} for intent '{declaration.Intent}'");
        }

        Console.WriteLine($"✅ Tool registry initialized with {toolTypesByIntent.Count} tool(s)");
        Console.WriteLine();

        // The registry and the duplicates, which the report prints together.
        return new Records.ToolDiscovery(toolTypesByIntent, registrationErrors);
    }

    /// <summary>
    /// Prints how the discovered tools line up against the discovered agents.
    /// Neither mismatch is fatal: an agent may legally have no native tool and a tool left behind by a renamed agent is dead code rather than a fault.
    /// </summary>
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

        // The two sides compared by intent, ignoring case as the registries do.
        HashSet<string> agentIntents = new(agentsByIntent.Keys, StringComparer.OrdinalIgnoreCase);
        HashSet<string> toolIntents = new(registry.Keys, StringComparer.OrdinalIgnoreCase);

        // The header is owed only if something is actually warned about, so it is written by whichever
        // of the two lists below turns out to be non-empty.
        bool warningsHeaderWritten = false;

        // An agent with no native tool is legal: an MCP-only agent acquires its competences at runtime,
        // so this says what a reader would otherwise have to guess from silence. Every intent in these three loops
        // is a key of the dictionary it is looked up in, so no lookup can miss.
        foreach (string intent in agentIntents.Except(toolIntents, StringComparer.OrdinalIgnoreCase))
        {
            WriteWarningsHeader();
            Console.WriteLine($"  ℹ️  Agent '{intent}' ({agentsByIntent[intent].Name}) has no native tool registered!");
        }

        // A tool built for an intent nobody claims: dead code left by a renamed or removed agent far
        // more often than something intended, so it is surfaced without stopping the deployment.
        foreach (string intent in toolIntents.Except(agentIntents, StringComparer.OrdinalIgnoreCase))
        {
            WriteWarningsHeader();
            Console.WriteLine($"  ⚠️  Tool '{registry[intent].Name}' provides intent '{intent}' but no agent handles this intent.");
        }

        // The pairs that hold. Printed too, so the absence of an agent from this list is itself readable.
        foreach (string intent in agentIntents.Intersect(toolIntents, StringComparer.OrdinalIgnoreCase))
            Console.WriteLine($"✅ Tool Registry: Agent '{intent}' → Tool '{registry[intent].Name}'");

        // Two tools claiming one agent, which unlike the warnings above stops the startup.
        if (registrationErrors.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Errors:");
            foreach (string error in registrationErrors)
                Console.WriteLine($"  ❌ {error}");
        }

        Console.WriteLine("========================================");
        Console.WriteLine();
        return;

        #region Utilities
        // Writes the warnings header the first time a warning is printed.
        void WriteWarningsHeader()
        {
            // The header is already on screen.
            if (warningsHeaderWritten)
                return;

            Console.WriteLine();
            Console.WriteLine("Warnings:");
            warningsHeaderWritten = true;
        }
        #endregion
    }

    /// <summary>
    /// Finds the tool type registered for an intent, ignoring case; null when the intent has none.
    /// Null is an expected outcome: the agent of that intent runs on the base tools alone or on MCP.
    /// </summary>
    public Type? FindToolTypeForIntent(string intent)
    {
        // A blank intent names no agent, so it has no tool; any other is looked up lowercased, as registered.
        return string.IsNullOrWhiteSpace(intent)
            ? null
            : intentToToolType.Value.GetValueOrDefault(intent.ToLowerInvariant());
    }

    /// <summary>Every registered tool type by intent, which the agent registry checks against its agents at startup.</summary>
    public IReadOnlyDictionary<string, Type> GetAllRegisteredTools()
    {
        // The first call runs the scan.
        return intentToToolType.Value;
    }
}