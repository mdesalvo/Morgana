using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Morgana.AI.Abstractions;
using Morgana.AI.Adapters;
using Morgana.AI.Attributes;
using Morgana.AI.Interfaces;

namespace Morgana.AI.Services;

/// <summary>
/// Discovers agents via [HandlesIntent] attribute with bidirectional validation.
/// Scans assemblies for MorganaAgent classes; validates that intents in config have agents, that agents in code have
/// config and that every declared peer consultation names an existing colleague. Performs LLM tier validation; throws on any mismatch.
/// </summary>
public class HandlesIntentAgentRegistryService : IAgentRegistryService
{
    /// <summary>
    /// Source of the configured intents, i.e. the other half of the bidirectional check: every
    /// intent it declares must be matched by a <c>[HandlesIntent]</c> agent and vice versa.
    /// </summary>
    private readonly IAgentConfigurationService agentConfigService;

    /// <summary>
    /// Validates each discovered agent's <c>[RequiresLLMTier]</c> against the active provider's
    /// configured tiers. A separate collaborator because tier validation is a distinct concern
    /// from intent↔agent matching, even though both run in the same startup pass.
    /// </summary>
    private readonly ILLMTierValidationService llmTierValidationService;

    /// <summary>
    /// Application configuration, read for the instances a consultation may name. Held here because a
    /// colleague published elsewhere is declared in configuration and not in code, so the startup
    /// check has nowhere else to learn that the name resolves to anything.
    /// </summary>
    private readonly IConfiguration configuration;

    /// <summary>
    /// Source of the native tool types, whose methods are weighed against what agents.json declares
    /// that they return.
    /// </summary>
    private readonly IToolRegistryService toolRegistryService;

    /// <summary>
    /// Registry mapping intent names to agent types.
    /// Built during service initialization via assembly scanning.
    /// Case-insensitive string comparison for intent matching.
    /// </summary>
    private readonly Lazy<Dictionary<string, Type>> intentToAgentType;

    /// <summary>Discovers agents and runs bidirectional intent↔agent validation (lazily — see field above).</summary>
    /// <param name="agentConfigService">Loads intent configuration from agents.json.</param>
    /// <param name="llmTierValidationService">Validates each agent's [RequiresLLMTier], delegated as a separate concern.</param>
    /// <param name="configuration">Application configuration, read for the declared instances.</param>
    /// <param name="toolRegistryService">Finds the native tool type of each intent, for the check of what its tools return.</param>
    /// <exception cref="InvalidOperationException">Validation fails: missing agents or missing configuration.</exception>
    public HandlesIntentAgentRegistryService(
        IAgentConfigurationService agentConfigService,
        ILLMTierValidationService llmTierValidationService,
        IConfiguration configuration,
        IToolRegistryService toolRegistryService)
    {
        this.agentConfigService = agentConfigService;
        this.llmTierValidationService = llmTierValidationService;
        this.configuration = configuration;
        this.toolRegistryService = toolRegistryService;

        // The scan must see every plugin assembly. DI construction order does not guarantee they have
        // all loaded, so the registry is built on first use rather than here.
        intentToAgentType = new Lazy<Dictionary<string, Type>>(InitializeRegistry);
    }

    /// <summary>
    /// Scans every loaded assembly for <see cref="MorganaAgent"/> subclasses declaring an intent
    /// and returns the intent-to-type map, without validating it.
    /// </summary>
    /// <returns>Intent to agent type, case-insensitive; agents without <c>[HandlesIntent]</c> are skipped.</returns>
    public static Dictionary<string, Type> DiscoverAgents()
    {
        // The roster of agents this installation answers with, one per intent. The two spellings that
        // must meet here are typed by hand in different files, so casing is not allowed to part them.
        Dictionary<string, Type> registry = new(StringComparer.OrdinalIgnoreCase);

        // Every assembly in the process, since a domain arrives as a plugin DLL loaded before this runs.
        IEnumerable<Type> morganaAgentTypes = AppDomain.CurrentDomain.GetAssemblies()
            // A runtime-generated assembly holds no agent an author wrote.
            .Where(a => !a.IsDynamic)
            .SelectMany(a =>
            {
                try
                {
                    return a.GetTypes();
                }
                catch (ReflectionTypeLoadException)
                {
                    // An assembly whose dependencies are incomplete costs only its own types: the scan
                    // goes on through the rest rather than failing over somebody else's broken plugin.
                    return [];
                }
            })
            // Concrete agents only. An abstract base is scaffolding a domain author shares between
            // agents, never an agent that answers.
            .Where(t => t is { IsClass: true, IsAbstract: false } && t.IsSubclassOf(typeof(MorganaAgent)));

        foreach (Type? morganaAgentType in morganaAgentTypes)
        {
            // An agent without [HandlesIntent] is skipped in silence. Two agents claiming ONE intent is
            // a real defect this hides: the later in reflection order shadows the earlier. That order
            // is undocumented, so which of the two survives is not even stable across runs.
            HandlesIntentAttribute? handlesIntentAttribute = morganaAgentType.GetCustomAttribute<HandlesIntentAttribute>();
            if (handlesIntentAttribute != null)
                registry[handlesIntentAttribute.Intent] = morganaAgentType;
        }

        // Unvalidated on purpose: the host reads this before the container exists, to publish one A2A
        // endpoint per agent, where throwing would refuse a deployment over a check it has not run yet.
        return registry;
    }

    /// <summary>
    /// Discovers the agents, then refuses a deployment whose declarations do not hold together.
    /// </summary>
    /// <returns>Intent to agent type, validated.</returns>
    /// <exception cref="InvalidOperationException">One or more declarations do not hold together.</exception>
    private Dictionary<string, Type> InitializeRegistry()
    {
        // The same map the host already used to publish one A2A endpoint per agent. Here it is weighed
        // for the first time, before any conversation can reach one of those agents.
        Dictionary<string, Type> registry = DiscoverAgents();

        // The domain's own word on what it answers for. Everything below weighs the code against it.
        List<Records.IntentDefinition> configuredIntents = agentConfigService.GetIntentsAsync().GetAwaiter().GetResult();

        // Every check runs before anything is thrown, so a misconfigured deployment reads its whole
        // list of problems at once instead of one category per restart cycle.
        List<string> validationErrors =
        [
            .. ValidateIntentCoverage(registry, configuredIntents),
            .. ValidateDeclaredTiers(registry),
            .. ValidatePeerDeclarations(registry),
            .. ValidateToolContracts(registry),
            .. ValidateWorkflowDeclarations(registry)
        ];

        // One exception carrying every problem, so a deployment is fixed in one pass.
        if (validationErrors.Count > 0)
            throw new InvalidOperationException(string.Join(Environment.NewLine, validationErrors));

        return registry;
    }

    /// <summary>
    /// Weighs the configured intents against the agents that declare them, in both directions.
    /// </summary>
    /// <remarks>
    /// The two failures are opposite halves of one contract. An intent nobody handles reaches the
    /// classifier then routes nowhere; an agent nobody declared is never reached at all.
    /// </remarks>
    /// <param name="registry">The discovered intent-to-type map.</param>
    /// <param name="configuredIntents">The intents declared in <c>agents.json</c>.</param>
    /// <returns>One message per direction that does not hold, empty when both do.</returns>
    private static List<string> ValidateIntentCoverage(
        Dictionary<string, Type> registry,
        List<Records.IntentDefinition> configuredIntents)
    {
        List<string> errors = [];
        HashSet<string> registeredIntents = [.. registry.Keys];

        // Every configured intent is a modelled agent and is owed an agent. The one intent that is
        // not — the complement of the domain — never reaches here: it belongs to the classifier and
        // is described in its prompt, so no domain declares it and none has to be excused for it.
        HashSet<string> classifierIntents = [.. configuredIntents.Select(intent => intent.Name)];

        // Offered to the classifier with nobody behind it: the router would answer its
        // unrecognized-intent fallback for a request the domain claims to serve.
        List<string> unhandledIntents = [.. classifierIntents.Except(registeredIntents)];
        if (unhandledIntents.Count > 0)
            errors.Add($"There are intents not handled by any Morgana agent: {string.Join(", ", unhandledIntents)}");

        // Written in code with nothing routing to it: the classifier has never heard that name, so the
        // agent is unreachable however correct it is.
        List<string> undeclaredIntents = [.. registeredIntents.Except(classifierIntents)];
        if (undeclaredIntents.Count > 0)
            errors.Add($"There are Morgana agents handling an undeclared intent: {string.Join(", ", undeclaredIntents)}");

        return errors;
    }

    /// <summary>
    /// Collects what the tier validator refuses, as messages rather than as a thrown exception.
    /// </summary>
    /// <param name="registry">The discovered intent-to-type map.</param>
    /// <returns>The validator's own message, or nothing when every declared tier is configured.</returns>
    private List<string> ValidateDeclaredTiers(Dictionary<string, Type> registry)
    {
        try
        {
            llmTierValidationService.ValidateAgentTiers(registry);
            return [];
        }
        catch (InvalidOperationException ex)
        {
            // Turned into a message so a tier problem cannot hide an intent problem: both are reported.
            return [ex.Message];
        }
    }

    /// <summary>
    /// Refuses a <c>[ConsultsAgent]</c> declaration naming a colleague that cannot be reached.
    /// </summary>
    /// <param name="registry">The discovered map, which is also the roster of colleagues of this installation.</param>
    /// <returns>One message per declaration that does not hold, empty when all of them do.</returns>
    private List<string> ValidatePeerDeclarations(Dictionary<string, Type> registry)
    {
        List<string> errors = [];

        // A colleague published elsewhere is declared in configuration rather than in code, so code
        // alone cannot say whether the name on the attribute resolves to anything.
        List<Records.PartnerOptions> partners = ConfigurationAgentDirectoryService.ResolvePartners(configuration);

        foreach ((string declaredIntent, Type agentType) in registry)
        {
            // Uniqueness is per agent: two agents may each hold a colleague offered under one name.
            HashSet<string> declaredColleagues = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (ConsultsAgentAttribute consultsAgent in agentType.GetCustomAttributes<ConsultsAgentAttribute>())
            {
                // What must be unique is the name the colleague is offered under, not the pair that
                // produced it: systems are named by people ("Newco Finance") while a function name is
                // constrained, so two distinct declarations can sanitize to one function. Derived by the
                // very method the adapter will use, so the check and the tool list cannot disagree.
                string peerFunctionName = MorganaAgentAdapter.ToFunctionName(
                    new Records.PeerReference(consultsAgent.Intent, consultsAgent.Instance));

                // How the colleague is named back to whoever has to fix the declaration.
                string colleague = consultsAgent.Instance is null
                    ? $"'{consultsAgent.Intent}'"
                    : $"'{consultsAgent.Intent}' at partner '{consultsAgent.Instance}'";

                // A colleague of this installation: the registry knows every agent, so this is settled here.
                if (consultsAgent.Instance is null)
                {
                    if (string.Equals(consultsAgent.Intent, declaredIntent, StringComparison.OrdinalIgnoreCase))
                        errors.Add($"Agent '{agentType.Name}' declares a consultation of itself ('{declaredIntent}')");
                    else if (!registry.ContainsKey(consultsAgent.Intent))
                        errors.Add($"Agent '{agentType.Name}' declares a consultation of '{consultsAgent.Intent}', which no Morgana agent handles");
                }

                // One published elsewhere: only this side of the wire is checkable.
                else if (ValidateConsultablePartner(partners, consultsAgent.Instance, agentType.Name, colleague) is { } partnerError)
                {
                    errors.Add(partnerError);
                }

                // Two colleagues folding to one function name would reach the provider as a duplicate
                // tool, which it refuses outright — on the first conversation, not here.
                if (!declaredColleagues.Add(peerFunctionName))
                    errors.Add($"Agent '{agentType.Name}' declares a consultation of {colleague} under a name it already offers another colleague under ('{peerFunctionName}')");
            }
        }

        return errors;
    }

    /// <summary>
    /// Refuses a tool class that does not declare its tools completely and a prompt that still declares them in JSON.
    /// </summary>
    /// <param name="registry">The discovered intent-to-agent map, which is the roster of intents to weigh.</param>
    /// <returns>One message per violation, empty when every contract holds.</returns>
    private List<string> ValidateToolContracts(Dictionary<string, Type> registry)
    {
        List<string> errors = [];
        List<Records.Prompt> prompts = agentConfigService.GetAgentPromptsAsync().GetAwaiter().GetResult();

        // Every domain prompt is weighed, including those of intents with no tool type: a leftover
        // declaration there is ignored all the same.
        errors.AddRange(ValidateNoDeclarationsInJson(prompts));

        foreach (string intent in registry.Keys)
        {
            // An intent without a tool type is the registry's own warning path: no class, no contract to weigh.
            Type? toolType = toolRegistryService.FindToolTypeForIntent(intent);
            if (toolType is null)
                continue;

            errors.AddRange(ValidateToolContract(intent, toolType));
        }

        return errors;
    }

    /// <summary>
    /// Refuses a domain prompt that still carries a <c>Tools</c> or a <c>Workflows</c> declaration.
    /// </summary>
    /// <remarks>
    /// Tools and workflows are declared on their classes, so a declaration left in JSON would be silently ignored.
    /// </remarks>
    /// <param name="prompts">The domain prompts that agents.json files declare.</param>
    /// <returns>One message per prompt and per key that it carries, empty when none does.</returns>
    public static List<string> ValidateNoDeclarationsInJson(IEnumerable<Records.Prompt> prompts)
    {
        List<string> errors = [];

        foreach (Records.Prompt prompt in prompts)
        {
            bool Declares(string key) => prompt.AdditionalProperties.Any(properties => properties.ContainsKey(key));

            if (Declares(Constants.PromptProperties.Tools))
                errors.Add($"Prompt '{prompt.ID}' declares \"{Constants.PromptProperties.Tools}\" in agents.json: tools are declared on the tool class and the declaration would be ignored");

            if (Declares(Constants.PromptProperties.Workflows))
                errors.Add($"Prompt '{prompt.ID}' declares \"{Constants.PromptProperties.Workflows}\" in agents.json: workflows are declared on their class and the declaration would be ignored");
        }

        return errors;
    }

    /// <summary>
    /// Refuses a workflow class that would validate cleanly and then stall or bind nothing at run time.
    /// </summary>
    /// <param name="registry">The discovered intent-to-agent map, which is the roster of intents to weigh.</param>
    /// <returns>One message per violation, empty when every workflow holds.</returns>
    private List<string> ValidateWorkflowDeclarations(Dictionary<string, Type> registry)
    {
        List<string> errors = [];

        // A workflow naming an intent that no agent handles can never be launched.
        foreach (string intent in toolRegistryService.GetAllRegisteredWorkflows().Keys.Where(intent => !registry.ContainsKey(intent)))
            errors.Add($"A workflow declares intent '{intent}', which no agent handles");

        foreach ((string intent, Type agentType) in registry)
        {
            IReadOnlyList<Records.WorkflowDefinition> workflows = toolRegistryService.GetWorkflowDefinitions(intent);
            if (workflows.Count == 0)
                continue;

            errors.AddRange(ValidateWorkflows(
                intent,
                workflows,
                toolRegistryService.GetToolDefinitions(intent),
                agentType.GetCustomAttributes<UsesMCPServerAttribute>().Any()));
        }

        return errors;
    }

    /// <summary>
    /// Weighs the workflows of one intent against its tools and against one another.
    /// </summary>
    /// <remarks>
    /// An agent carrying <c>[UsesMCPServer]</c> may name a tool that startup cannot check, since its tools
    /// arrive at run time: such a tool can be a step and the source or the target of a carried value, which startup leaves unchecked since it declares no <c>Returns</c> or parameters.
    /// </remarks>
    /// <param name="intent">The intent that owns the workflows, named in the messages.</param>
    /// <param name="workflows">The workflows that the classes of the intent declare.</param>
    /// <param name="declaredTools">The domain tools that the intent's tool class declares.</param>
    /// <param name="usesMcpServer">Whether the agent acquires tools from an MCP server.</param>
    /// <returns>One message per violation, empty when every workflow holds.</returns>
    public static List<string> ValidateWorkflows(
        string intent,
        IReadOnlyList<Records.WorkflowDefinition> workflows,
        IEnumerable<Records.ToolDefinition> declaredTools,
        bool usesMcpServer)
    {
        List<string> errors = [];
        Dictionary<string, Records.ToolDefinition> tools = declaredTools
            .GroupBy(tool => tool.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        HashSet<string> workflowNames = new(StringComparer.Ordinal);

        foreach (Records.WorkflowDefinition workflow in workflows)
        {
            string subject = $"Workflow '{workflow.Name}' of intent '{intent}'";

            if (string.IsNullOrWhiteSpace(workflow.Name))
                errors.Add($"A workflow of intent '{intent}' has no name");
            else if (!workflowNames.Add(workflow.Name))
                errors.Add($"{subject} is declared more than once: a workflow name is unique per agent");

            if (string.IsNullOrWhiteSpace(workflow.Description))
                errors.Add($"{subject} has no description");

            // The first step is where the workflow starts, so a workflow without one has nothing to weigh.
            if (workflow.Steps is not { Count: > 0 })
            {
                errors.Add($"{subject} has no step");
                continue;
            }

            ValidateWorkflowSteps(subject, workflow, tools, usesMcpServer, errors);
            ValidateWorkflowEdges(subject, workflow, tools, usesMcpServer, errors);
        }

        return errors;
    }

    /// <summary>
    /// Weighs the steps of one workflow: their names, their tools and whether a path from the first step reaches them.
    /// </summary>
    private static void ValidateWorkflowSteps(
        string subject,
        Records.WorkflowDefinition workflow,
        Dictionary<string, Records.ToolDefinition> tools,
        bool usesMcpServer,
        List<string> errors)
    {
        if (workflow.Steps.Any(step => string.IsNullOrWhiteSpace(step.Name)))
            errors.Add($"{subject} has a step with no name");

        foreach (string duplicated in workflow.Steps
                     .Where(step => !string.IsNullOrWhiteSpace(step.Name))
                     .GroupBy(step => step.Name, StringComparer.Ordinal)
                     .Where(group => group.Count() > 1)
                     .Select(group => group.Key))
            errors.Add($"{subject}, step '{duplicated}': the name is declared more than once");

        foreach (Records.WorkflowStep step in workflow.Steps)
        {
            string stepSubject = $"{subject}, step '{step.Name}'";
            IReadOnlyList<string> stepTools = step.Tools ?? [];

            if (stepTools.Count == 0)
                errors.Add($"{stepSubject}: the step offers no tool");

            foreach (string tool in stepTools)
            {
                // The framework's own tools belong to no step: Reply closes every turn and a colleague is a
                // private method of the workflow, never one of its steps.
                if (tool is Constants.Tools.Reply or Constants.Tools.LaunchWorkflow
                    || tool.StartsWith(Constants.AgentToAgent.PeerFunctionNamePrefix, StringComparison.Ordinal))
                    errors.Add($"{stepSubject}: '{tool}' belongs to the framework and cannot be a step's tool");
                else if (!tools.ContainsKey(tool) && !usesMcpServer)
                    errors.Add($"{stepSubject}: the agent has no tool '{tool}'");
            }
        }

        // Steps that no path from the first one reaches would never be offered: the workflow could not mean them.
        HashSet<string> reached = ReachableFrom(workflow, workflow.Steps[0].Name);
        foreach (Records.WorkflowStep step in workflow.Steps.Skip(1).Where(step => !reached.Contains(step.Name)))
            errors.Add($"{subject}, step '{step.Name}': no path leads to it from '{workflow.Steps[0].Name}'");
    }

    /// <summary>
    /// Weighs the edges of one workflow: the tool each one follows and the values it carries.
    /// </summary>
    private static void ValidateWorkflowEdges(
        string subject,
        Records.WorkflowDefinition workflow,
        Dictionary<string, Records.ToolDefinition> tools,
        bool usesMcpServer,
        List<string> errors)
    {
        Dictionary<string, Records.WorkflowStep> stepsByName = workflow.Steps
            .Where(step => !string.IsNullOrWhiteSpace(step.Name))
            .GroupBy(step => step.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        HashSet<string> carried = new(StringComparer.Ordinal);

        // The engine follows the first edge it finds, so a transition declared twice would leave one of the two dead.
        foreach (IGrouping<(string Source, string Tool, bool OnFailure), Records.WorkflowEdge> group in workflow.Edges
                     .GroupBy(edge => (edge.Source, edge.Tool, edge.OnFailure))
                     .Where(group => group.Count() > 1))
            errors.Add($"{EdgeSubject(subject, group.First())}: the same transition is declared more than once");

        foreach (Records.WorkflowEdge edge in workflow.Edges)
        {
            string edgeSubject = EdgeSubject(subject, edge);

            if (stepsByName.TryGetValue(edge.Source, out Records.WorkflowStep? source) && !source.Tools.Contains(edge.Tool, StringComparer.Ordinal))
                errors.Add($"{edgeSubject}: '{edge.Tool}' is not a tool of step '{edge.Source}'");

            tools.TryGetValue(edge.Tool, out Records.ToolDefinition? sourceTool);
            stepsByName.TryGetValue(edge.Target, out Records.WorkflowStep? target);

            // A target tool that startup cannot see may take any parameter: a name cannot be refused on its account.
            bool targetHasUncheckableTool = usesMcpServer && (target?.Tools ?? []).Any(tool => !tools.ContainsKey(tool));

            foreach (string name in edge.Carrying)
            {
                carried.Add(name);

                if (!workflow.Parameters.Contains(name, StringComparer.Ordinal))
                {
                    errors.Add($"{edgeSubject}: '{name}' is not a public property of the workflow");
                    continue;
                }

                // A value flows from a returned field into a parameter, matched by name whatever the casing of either.
                if (sourceTool is not null
                    && !(sourceTool.Returns ?? []).Any(field => string.Equals(field.Name, name, StringComparison.OrdinalIgnoreCase)))
                    errors.Add($"{edgeSubject}: tool '{edge.Tool}' does not declare the returned field '{name}'");

                bool taken = (target?.Tools ?? [])
                    .Where(tools.ContainsKey)
                    .Any(tool => tools[tool].Parameters.Any(parameter => string.Equals(parameter.Name, name, StringComparison.OrdinalIgnoreCase)));
                if (!taken && !targetHasUncheckableTool)
                    errors.Add($"{edgeSubject}: no tool of step '{edge.Target}' takes a parameter '{name}'");
            }
        }

        // A property that no edge carries would stay empty for ever.
        foreach (string parameter in workflow.Parameters.Where(parameter => !carried.Contains(parameter)))
            errors.Add($"{subject}: property '{parameter}' is carried by no edge");
    }

    /// <summary>Names an edge in a message: where it leaves from and arrives at and the outcome that it follows.</summary>
    private static string EdgeSubject(string subject, Records.WorkflowEdge edge)
        => $"{subject}, edge '{edge.Source}' -> '{edge.Target}' on '{edge.Tool}'{(edge.OnFailure ? " (failure)" : string.Empty)}";

    /// <summary>
    /// The steps that a workflow can reach from one step by following its edges.
    /// </summary>
    private static HashSet<string> ReachableFrom(Records.WorkflowDefinition workflow, string from)
    {
        // The start counts as reached from the outset: the first step is where the workflow stands when launched.
        HashSet<string> reached = new([from], StringComparer.Ordinal);
        Queue<string> pending = new([from]);

        while (pending.TryDequeue(out string? current))
        {
            foreach (Records.WorkflowEdge edge in workflow.Edges.Where(candidate => candidate.Source == current))
            {
                if (reached.Add(edge.Target))
                    pending.Enqueue(edge.Target);
            }
        }

        return reached;
    }

    /// <summary>
    /// Weighs a tool class on its own: every tool method and parameter declared completely and the record each returns usable.
    /// </summary>
    /// <remarks>
    /// The class is the only declaration of its tools, so what the model reads and what a workflow engine
    /// reads is only as complete as the attributes found here.
    /// </remarks>
    /// <param name="intent">The intent that owns the tools, named in the messages.</param>
    /// <param name="toolType">The class implementing the tools.</param>
    /// <returns>One message per violation, empty when every contract holds.</returns>
    public static List<string> ValidateToolContract(string intent, Type toolType)
    {
        List<string> errors = [];
        IReadOnlyList<MethodInfo> methods = ProvidesToolForIntentRegistryService.GetToolMethods(toolType);

        // Two methods under one name reach the model as one tool declared twice.
        foreach (string duplicated in methods.GroupBy(method => method.Name, StringComparer.Ordinal).Where(group => group.Count() > 1).Select(group => group.Key))
            errors.Add($"Tool '{duplicated}' of intent '{intent}' is declared by more than one method of '{toolType.Name}': a tool name is unique");

        foreach (MethodInfo method in methods)
        {
            string subject = $"Tool '{method.Name}' of intent '{intent}'";

            if (string.IsNullOrWhiteSpace(method.GetCustomAttribute<DescriptionAttribute>()?.Description))
                errors.Add($"{subject} has no [Description]: the model reads it to know what the tool does");

            if (method.GetCustomAttribute<RequiresApprovalAttribute>() is null)
                errors.Add($"{subject} has no [RequiresApproval]: whether the user must approve each call is always declared");

            foreach (ParameterInfo parameter in method.GetParameters())
                ValidateToolParameter(subject, parameter, errors);

            ValidateToolReturn(subject, method, errors);
        }

        return errors;
    }

    /// <summary>
    /// Weighs one parameter of a tool method: its prose, its scope and the combinations that the framework cannot honour.
    /// </summary>
    /// <param name="subject">The tool, named in the messages.</param>
    /// <param name="parameter">The parameter to weigh.</param>
    /// <param name="errors">Receives one message per violation.</param>
    private static void ValidateToolParameter(string subject, ParameterInfo parameter, List<string> errors)
    {
        string parameterSubject = $"Parameter '{parameter.Name}' of {subject}";

        if (string.IsNullOrWhiteSpace(parameter.GetCustomAttribute<DescriptionAttribute>()?.Description))
            errors.Add($"{parameterSubject} has no [Description]: the model reads it to know what to pass");

        ToolParameterAttribute? declaration = parameter.GetCustomAttribute<ToolParameterAttribute>();
        if (declaration is null)
        {
            errors.Add($"{parameterSubject} has no [ToolParameter]: its scope is always declared");
            return;
        }

        // Only what the context holds can be shared, so a value asked of the user has nothing to share.
        if (declaration is { Scope: Records.ToolScope.Request, Shared: true })
            errors.Add($"{parameterSubject} is a request parameter marked as shared: only what the context holds is shared");

        if (declaration.Scope != Records.ToolScope.Context)
            return;

        // A context value that nobody holds stops the tool, so a default would never be used.
        if (parameter.HasDefaultValue)
            errors.Add($"{parameterSubject} is a context parameter with a default value: a context value that nobody holds stops the tool, so the default would never be used");

        // The context and the shared registry keep untyped text, so a value written as text by one agent
        // could reach another that declares a different type.
        if (parameter.ParameterType != typeof(string))
            errors.Add($"{parameterSubject} is a context parameter of type '{parameter.ParameterType.Name}': the context holds text, so it is a string");
    }

    /// <summary>
    /// Weighs the record that a tool method returns: a typed record, whose failure field when present allows null.
    /// </summary>
    /// <param name="subject">The tool, named in the messages.</param>
    /// <param name="method">The tool method.</param>
    /// <param name="errors">Receives one message per violation.</param>
    private static void ValidateToolReturn(string subject, MethodInfo method, List<string> errors)
    {
        Type? returnType = UnwrapReturnType(method.ReturnType);

        // A string or an object tells the schema nothing: the model would read a document that nobody declared.
        if (returnType is null || returnType == typeof(string) || returnType == typeof(object))
        {
            errors.Add($"{subject} returns '{method.ReturnType.Name}': a native tool returns a typed record");
            return;
        }

        JsonElement schema = MorganaToolAdapter.CreateReturnSchema(returnType);
        if (!schema.TryGetProperty("properties", out JsonElement properties))
        {
            errors.Add($"{subject} returns '{returnType.Name}', which has no properties: a native tool returns a typed record");
            return;
        }

        // The failure field is the one whose holding a value means the call failed, so a record that
        // cannot leave it empty would report every call as failed.
        if (properties.TryGetProperty(Constants.Workflows.FailureField, out JsonElement failureSchema) && !AllowsNull(failureSchema))
            errors.Add($"{subject} returns '{returnType.Name}' whose '{Constants.Workflows.FailureField}' property does not allow null: the failure field is nullable");
    }

    /// <summary>
    /// Takes the result type out of a <c>Task</c> or a <c>ValueTask</c>; null when the method returns no value.
    /// </summary>
    /// <param name="methodReturnType">The return type as the method declares it.</param>
    internal static Type? UnwrapReturnType(Type methodReturnType)
    {
        if (methodReturnType == typeof(void) || methodReturnType == typeof(Task) || methodReturnType == typeof(ValueTask))
            return null;

        return methodReturnType.IsGenericType
               && (methodReturnType.GetGenericTypeDefinition() == typeof(Task<>) || methodReturnType.GetGenericTypeDefinition() == typeof(ValueTask<>))
            ? methodReturnType.GetGenericArguments()[0]
            : methodReturnType;
    }

    /// <summary>
    /// Tells whether a property's schema accepts null, which is how a nullable reference or value type announces itself.
    /// </summary>
    /// <param name="propertySchema">The schema the return type derives for the property.</param>
    private static bool AllowsNull(JsonElement propertySchema)
    {
        if (!propertySchema.TryGetProperty("type", out JsonElement type))
            return true;

        return type.ValueKind == JsonValueKind.Array
            ? type.EnumerateArray().Any(entry => entry.GetString() == "null")
            : type.GetString() == "null";
    }

    /// <summary>
    /// Checks that the partner an attribute names is one this installation may actually call. What
    /// that partner publishes is its card's word, read on the first consultation and not checked here;
    /// that its address and key are usable is checked with the rest of the entry, at publication.
    /// </summary>
    /// <param name="partners">Partners declared under <c>Morgana:AgentToAgent:Partners</c>, parked ones already dropped.</param>
    /// <param name="instanceName">Partner named on the attribute.</param>
    /// <param name="agentName">Agent carrying the declaration, named in the diagnostics.</param>
    /// <param name="colleague">The colleague as the caller renders it, reused in the messages.</param>
    /// <returns>The first thing missing, or <c>null</c> when nothing is.</returns>
    private static string? ValidateConsultablePartner(
        List<Records.PartnerOptions> partners,
        string instanceName,
        string agentName,
        string colleague)
    {
        // The entry that says where that partner answers. Trimmed on the configuration side because the
        // two spellings are authored in different files by different hands.
        Records.PartnerOptions? partner = partners
            .FirstOrDefault(candidate => string.Equals(candidate.Name?.Trim(), instanceName, StringComparison.OrdinalIgnoreCase));

        // The mismatch is almost always a name written twice, so the declared ones are listed back. A
        // parked partner is absent from this list and reads the same way, which is what parking means.
        if (partner is null)
        {
            return $"Agent '{agentName}' declares a consultation of {colleague}, which is not declared under Morgana:AgentToAgent:Partners "
                 + $"(declared: {(partners.Count > 0 ? string.Join(", ", partners.Select(declared => $"'{declared.Name}'")) : "none")}). "
                 + "The name on the attribute and the Name on the entry must be the same, spelling and spacing included";
        }

        // Declared but not open to being consulted: the relationship exists in the other direction only,
        // so the attribute claims a reach the entry beside it refuses.
        if (partner.OutboundPolicy?.Enabled != true)
        {
            return $"Agent '{agentName}' declares a consultation of {colleague}, but partner '{partner.Name}' declares no "
                 + "\"OutboundPolicy\": { \"Enabled\": true }: this installation may not call it";
        }

        return null;
    }

    /// <summary>
    /// Resolves agent type for an intent (case-insensitive; null if not found).
    /// Returns null to allow RouterActor to provide user-friendly error messages.
    /// </summary>
    /// <param name="intent">Intent name to resolve</param>
    /// <returns>Agent type or null if unrecognized</returns>
    public Type? ResolveAgentFromIntent(string intent)
        => intentToAgentType.Value.GetValueOrDefault(intent);

    /// <summary>All registered intent names — RouterActor uses this to pre-create agents at startup.</summary>
    public IEnumerable<string> GetAllIntents()
        => intentToAgentType.Value.Keys;
}