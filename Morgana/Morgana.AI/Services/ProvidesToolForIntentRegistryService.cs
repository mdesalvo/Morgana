using System.Collections.Concurrent;
using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Morgana.AI.Abstractions;
using Morgana.AI.Adapters;
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
    /// The projected definitions per intent, kept because agents are created per conversation while a
    /// tool class does not change under a running process.
    /// </summary>
    private readonly ConcurrentDictionary<string, IReadOnlyList<Records.ToolDefinition>> toolDefinitionsByIntent = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The workflows that classes declare, projected once per intent: a class does not change under a running process.
    /// </summary>
    private readonly Lazy<Dictionary<string, IReadOnlyList<Records.WorkflowDefinition>>> workflowsByIntent;

    /// <summary>
    /// Initializes a new instance of ProvidesToolForIntentRegistryService.
    /// Performs tool discovery and validation with comprehensive diagnostic output.
    /// </summary>
    /// <param name="logger">Logger instance for diagnostic information</param>
    public ProvidesToolForIntentRegistryService(ILogger logger)
    {
        this.logger = logger;

        intentToToolType = new Lazy<Dictionary<string, Type>>(InitializeRegistry);
        workflowsByIntent = new Lazy<Dictionary<string, IReadOnlyList<Records.WorkflowDefinition>>>(DiscoverWorkflows);
    }

    /// <summary>
    /// Finds every <see cref="MorganaWorkflow"/> that declares an intent and projects each one once.
    /// </summary>
    /// <returns>The definitions per intent, lowercased, case-insensitive.</returns>
    /// <exception cref="InvalidOperationException">A workflow class cannot be instantiated.</exception>
    private Dictionary<string, IReadOnlyList<Records.WorkflowDefinition>> DiscoverWorkflows()
    {
        Console.WriteLine("🔍 Scanning assemblies for MorganaWorkflow implementations...");

        Dictionary<string, List<Records.WorkflowDefinition>> collected = new(StringComparer.OrdinalIgnoreCase);

        // Every assembly in the process, since a domain's workflows arrive in a plugin DLL that
        // PluginLoaderService has already loaded by the time this runs.
        IEnumerable<Type> workflowTypes = AppDomain.CurrentDomain.GetAssemblies()
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
                    // plugin must not hide the workflows of every other one.
                    logger.LogWarning("Could not load types from assembly {ArgFullName}: {ExMessage}", a.FullName, ex.Message);
                    return [];
                }
            })
            // Concrete workflows that declare which agent they belong to. A workflow without the
            // attribute belongs to no agent, so nothing could ever launch it.
            .Where(t => t is { IsClass: true, IsAbstract: false } && t.IsSubclassOf(typeof(MorganaWorkflow)))
            .Where(t => t.GetCustomAttribute<ProvidesWorkflowForIntentAttribute>() != null);

        foreach (Type workflowType in workflowTypes)
        {
            ProvidesWorkflowForIntentAttribute declaration = workflowType.GetCustomAttribute<ProvidesWorkflowForIntentAttribute>()!;

            // Lowercased on the way in, since an intent is typed by hand here and on the agent.
            string intent = declaration.Intent.ToLowerInvariant();

            // Discovery runs while startup validates the agents, so a class that cannot be built stops the host
            // there instead of leaving its agent without the workflow in front of a user.
            MorganaWorkflow workflow;
            try
            {
                workflow = (MorganaWorkflow)Activator.CreateInstance(workflowType)!;
            }
            catch (Exception ex) when (ex is MissingMethodException or TargetInvocationException)
            {
                throw new InvalidOperationException(
                    $"Workflow class '{workflowType.Name}' of intent '{intent}' cannot be instantiated: {(ex.InnerException ?? ex).Message}", ex);
            }

            if (!collected.TryGetValue(intent, out List<Records.WorkflowDefinition>? definitions))
                collected[intent] = definitions = [];
            definitions.Add(workflow.ToDefinition());

            Console.WriteLine($"  📦 Registered workflow: {workflowType.Name} for intent '{declaration.Intent}'");
        }

        Console.WriteLine($"✅ Workflow registry initialized with {collected.Values.Sum(definitions => definitions.Count)} workflow(s)");
        Console.WriteLine();

        return collected.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<Records.WorkflowDefinition>)pair.Value, StringComparer.OrdinalIgnoreCase);
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
            // Concrete tools that declare which agent they belong to. A tool without the attribute
            // belongs to no agent, so nothing could ever reach it.
            .Where(t => t is { IsClass: true, IsAbstract: false } && t.IsSubclassOf(typeof(MorganaTool)))
            .Where(t => t.GetCustomAttribute<ProvidesToolForIntentAttribute>() != null);

        foreach (Type toolType in toolTypes)
        {
            // Which agent this tool belongs to, in the author's own words. Never absent: a type that does
            // not declare one was filtered out above, so the filter is the guard.
            ProvidesToolForIntentAttribute declaration = toolType.GetCustomAttribute<ProvidesToolForIntentAttribute>()!;

            // Lowercased on the way in, since an intent is typed by hand here and in agents.json.
            string intent = declaration.Intent.ToLowerInvariant();

            // The first tool found keeps the agent. Overwriting would hand the intent to whichever
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

        // The pairs that hold. Printed too, so the absence of an agent from this list is itself readable.
        foreach (string intent in agentIntents.Intersect(toolIntents, StringComparer.OrdinalIgnoreCase))
            Console.WriteLine($"✅ Tool Registry: Agent '{intent}' → Tool '{registry.GetValueOrDefault(intent)?.Name ?? "unknown"}'");

        // Two tools claiming one agent, which unlike the warnings above is a defect somebody must fix:
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
    /// has no native tool and runs on framework tool alone (Reply) or MCP.
    /// </remarks>
    public Type? FindToolTypeForIntent(string intent)
    {
        return string.IsNullOrWhiteSpace(intent)
            ? null
            : intentToToolType.Value.GetValueOrDefault(intent.ToLowerInvariant());
    }

    /// <inheritdoc />
    public IReadOnlyList<Records.ToolDefinition> GetToolDefinitions(string intent)
    {
        if (FindToolTypeForIntent(intent) is not { } toolType)
            return [];

        return toolDefinitionsByIntent.GetOrAdd(intent, _ => ProjectToolDefinitions(toolType));
    }

    /// <inheritdoc />
    public IReadOnlyList<Records.WorkflowDefinition> GetWorkflowDefinitions(string intent)
        => string.IsNullOrWhiteSpace(intent) ? [] : workflowsByIntent.Value.GetValueOrDefault(intent) ?? [];

    /// <inheritdoc />
    public IReadOnlyDictionary<string, IReadOnlyList<Records.WorkflowDefinition>> GetAllRegisteredWorkflows()
        => workflowsByIntent.Value;

    /// <summary>
    /// Lists the methods of a tool class that are tools: the public instance methods that it declares
    /// itself, in declaration order.
    /// </summary>
    /// <remarks>
    /// An override and a property accessor are not declared tools, so <c>Reply</c> and the members of
    /// <c>object</c> stay out; a helper is left out by not being public.
    /// </remarks>
    /// <param name="toolType">The <see cref="MorganaTool"/> subclass to read.</param>
    public static IReadOnlyList<MethodInfo> GetToolMethods(Type toolType)
    {
        // The metadata token follows the source order, which reflection does not promise to preserve.
        return
        [
            .. toolType
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(method => !method.IsSpecialName && method.GetBaseDefinition().DeclaringType == method.DeclaringType)
                .OrderBy(method => method.MetadataToken)
        ];
    }

    /// <summary>
    /// Projects the definitions that a tool class declares through its methods, parameters and returned records.
    /// </summary>
    /// <remarks>
    /// Projects what the class carries and never throws on one that is malformed: an absent attribute
    /// leaves its field at the default, since refusing the class is the startup check's job.
    /// </remarks>
    /// <param name="toolType">The <see cref="MorganaTool"/> subclass to project.</param>
    public static IReadOnlyList<Records.ToolDefinition> ProjectToolDefinitions(Type toolType)
    {
        return [.. GetToolMethods(toolType).Select(ProjectToolDefinition)];
    }

    /// <summary>Projects one tool method into the definition that the adapter and the composer read.</summary>
    /// <param name="method">A tool method as <see cref="GetToolMethods"/> lists it.</param>
    private static Records.ToolDefinition ProjectToolDefinition(MethodInfo method)
    {
        List<Records.ToolParameter> parameters =
        [
            .. method.GetParameters().Select(parameter => new Records.ToolParameter(
                parameter.Name ?? string.Empty,
                parameter.GetCustomAttribute<DescriptionAttribute>()?.Description ?? string.Empty,
                !parameter.HasDefaultValue,
                parameter.GetCustomAttribute<ToolParameterAttribute>()?.Scope == Records.ToolScope.Context
                    ? Constants.Scopes.Context
                    : Constants.Scopes.Request,
                parameter.GetCustomAttribute<ToolParameterAttribute>()?.Shared ?? false))
        ];

        return new Records.ToolDefinition(
            method.Name,
            method.GetCustomAttribute<DescriptionAttribute>()?.Description ?? string.Empty,
            parameters,
            Reserved: false,
            RequiresExecutionApproval: method.GetCustomAttribute<RequiresApprovalAttribute>()?.Required ?? false,
            Returns: ProjectReturns(method.ReturnType));
    }

    /// <summary>
    /// Projects the top-level fields of the record a tool returns; null when the method returns no record.
    /// </summary>
    /// <param name="methodReturnType">The return type as the method declares it.</param>
    private static List<Records.ToolReturn>? ProjectReturns(Type methodReturnType)
    {
        Type? returnType = HandlesIntentAgentRegistryService.UnwrapReturnType(methodReturnType);
        if (returnType is null)
            return null;

        JsonElement schema = MorganaToolAdapter.CreateReturnSchema(returnType);
        if (!schema.TryGetProperty("properties", out JsonElement properties))
            return null;

        return
        [
            .. properties.EnumerateObject().Select(property => new Records.ToolReturn(
                property.Name,
                property.Value.TryGetProperty("description", out JsonElement description) ? description.GetString() ?? string.Empty : string.Empty,
                property.Name == Constants.Workflows.FailureField))
        ];
    }

    /// <summary>All registered tool types keyed by intent — diagnostics/validation/testing enumeration.</summary>
    public IReadOnlyDictionary<string, Type> GetAllRegisteredTools()
    {
        return intentToToolType.Value;
    }
}