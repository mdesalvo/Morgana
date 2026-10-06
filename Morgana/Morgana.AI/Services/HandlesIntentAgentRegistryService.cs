using System.Globalization;
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
    /// Refuses a native tool whose returned record and <c>Returns</c> declaration in agents.json do not describe the same thing.
    /// </summary>
    /// <param name="registry">The discovered intent-to-agent map, which is the roster of intents to weigh.</param>
    /// <returns>One message per violation, empty when every contract holds.</returns>
    private List<string> ValidateToolContracts(Dictionary<string, Type> registry)
    {
        List<string> errors = [];
        List<Records.Prompt> prompts = agentConfigService.GetAgentPromptsAsync().GetAwaiter().GetResult();

        foreach (string intent in registry.Keys)
        {
            // An intent without a tool type is the registry's own warning path. An intent without a
            // prompt is already refused by the coverage check. Neither has a contract to weigh here.
            Type? toolType = toolRegistryService.FindToolTypeForIntent(intent);
            Records.Prompt? prompt = prompts.FirstOrDefault(candidate => string.Equals(candidate.ID, intent, StringComparison.OrdinalIgnoreCase));
            if (toolType is null || prompt is null)
                continue;

            errors.AddRange(ValidateToolContract(intent, toolType, prompt.GetAdditionalPropertyOrDefault<Records.ToolDefinition[]>(Constants.PromptProperties.Tools, [])));
        }

        return errors;
    }

    /// <summary>
    /// Refuses a workflow declaration that would validate cleanly and then stall or bind nothing at run time.
    /// </summary>
    /// <param name="registry">The discovered intent-to-agent map, which is the roster of intents to weigh.</param>
    /// <returns>One message per violation, empty when every workflow holds.</returns>
    private List<string> ValidateWorkflowDeclarations(Dictionary<string, Type> registry)
    {
        List<string> errors = [];
        List<Records.Prompt> prompts = agentConfigService.GetAgentPromptsAsync().GetAwaiter().GetResult();

        foreach ((string intent, Type agentType) in registry)
        {
            Records.Prompt? prompt = prompts.FirstOrDefault(candidate => string.Equals(candidate.ID, intent, StringComparison.OrdinalIgnoreCase));
            if (prompt is null)
                continue;

            Records.WorkflowDefinition[] workflows = prompt.GetAdditionalPropertyOrDefault<Records.WorkflowDefinition[]>(Constants.PromptProperties.Workflows, []);
            if (workflows.Length == 0)
                continue;

            errors.AddRange(ValidateWorkflows(
                intent,
                workflows,
                prompt.GetAdditionalPropertyOrDefault<Records.ToolDefinition[]>(Constants.PromptProperties.Tools, []),
                agentType.GetCustomAttributes<UsesMCPServerAttribute>().Any()));
        }

        return errors;
    }

    /// <summary>
    /// Weighs the workflows of one intent against its tools and against one another.
    /// </summary>
    /// <remarks>
    /// An agent carrying <c>[UsesMCPServer]</c> may name a tool that startup cannot check, since its tools
    /// arrive at run time: such a tool can be a step but never the source of a bound value, because it declares no <c>Returns</c>.
    /// </remarks>
    /// <param name="intent">The intent that owns the workflows, named in the messages.</param>
    /// <param name="workflows">The workflows that agents.json declares for the intent.</param>
    /// <param name="declaredTools">The domain tools that agents.json declares for the intent.</param>
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

            if (workflow.Steps is not { Count: > 0 })
            {
                errors.Add($"{subject} has no step");
                continue;
            }

            ValidateWorkflowSteps(subject, workflow, tools, usesMcpServer, errors);
        }

        return errors;
    }

    /// <summary>
    /// Weighs the steps of one workflow: their names, their tools, their links and the values they bind.
    /// </summary>
    private static void ValidateWorkflowSteps(
        string subject,
        Records.WorkflowDefinition workflow,
        Dictionary<string, Records.ToolDefinition> tools,
        bool usesMcpServer,
        List<string> errors)
    {
        HashSet<string> stepNames = new(StringComparer.Ordinal);

        foreach (Records.WorkflowStep step in workflow.Steps)
        {
            if (string.IsNullOrWhiteSpace(step.Name))
                errors.Add($"{subject} has a step with no name");
            else if (step.Name == Constants.Workflows.End)
                errors.Add($"{subject} names a step '{Constants.Workflows.End}', which is the reserved target that ends the workflow");
            else if (!stepNames.Add(step.Name))
                errors.Add($"{subject} declares the step '{step.Name}' more than once");
        }

        foreach (Records.WorkflowStep step in workflow.Steps)
        {
            string stepSubject = $"{subject}, step '{step.Name}'";
            IReadOnlyList<string> stepTools = step.Tools ?? [];

            if (stepTools.Count == 0)
                errors.Add($"{stepSubject}, names no tool");

            foreach (string tool in stepTools)
            {
                // The framework's own tools belong to no step: Reply closes every turn and a colleague is a
                // private method of the workflow, never one of its steps.
                if (tool is Constants.Tools.Reply or Constants.Tools.LaunchWorkflow
                    || tool.StartsWith(Constants.AgentToAgent.PeerFunctionNamePrefix, StringComparison.Ordinal))
                    errors.Add($"{stepSubject}, names '{tool}', which a step may never name");
                else if (!tools.ContainsKey(tool) && !usesMcpServer)
                    errors.Add($"{stepSubject}, names the tool '{tool}', which the agent does not declare");
            }

            foreach ((string linkKind, IReadOnlyDictionary<string, string>? links) in new[] { ("Next", step.Next), ("OnFailure", step.OnFailure) })
            {
                foreach ((string tool, string target) in links ?? new Dictionary<string, string>())
                {
                    if (!stepTools.Contains(tool))
                        errors.Add($"{stepSubject}, \"{linkKind}\" names '{tool}', which is not a tool of the step");

                    if (target != Constants.Workflows.End && !stepNames.Contains(target))
                        errors.Add($"{stepSubject}, \"{linkKind}\" leads '{tool}' to '{target}', which is neither a step of the workflow nor '{Constants.Workflows.End}'");
                }
            }
        }

        // Steps that no path from the first one reaches would never be offered: the workflow could not mean them.
        Dictionary<string, HashSet<string>> reach = workflow.Steps
            .Where(step => !string.IsNullOrWhiteSpace(step.Name))
            .DistinctBy(step => step.Name, StringComparer.Ordinal)
            .ToDictionary(step => step.Name, step => ReachableFrom(workflow, step.Name), StringComparer.Ordinal);

        foreach (Records.WorkflowStep step in workflow.Steps.Skip(1).Where(step => !string.IsNullOrWhiteSpace(step.Name)))
        {
            if (reach.TryGetValue(workflow.Steps[0].Name ?? string.Empty, out HashSet<string>? fromFirst) && !fromFirst.Contains(step.Name))
                errors.Add($"{subject} has the step '{step.Name}', which no path from its first step '{workflow.Steps[0].Name}' reaches");
        }

        foreach (Records.WorkflowStep step in workflow.Steps)
            ValidateStepArguments($"{subject}, step '{step.Name}'", step, reach, workflow, tools, errors);
    }

    /// <summary>
    /// Weighs the values a step binds: the parameter they fill and the earlier result they are read from.
    /// </summary>
    private static void ValidateStepArguments(
        string stepSubject,
        Records.WorkflowStep step,
        Dictionary<string, HashSet<string>> reach,
        Records.WorkflowDefinition workflow,
        Dictionary<string, Records.ToolDefinition> tools,
        List<string> errors)
    {
        IReadOnlyList<string> stepTools = step.Tools ?? [];

        // A tool that startup cannot see may take any parameter: a key cannot be refused on its account.
        bool hasUncheckableTool = stepTools.Any(tool => !tools.ContainsKey(tool));

        foreach ((string parameter, string source) in step.BoundArguments())
        {
            if (!hasUncheckableTool
                && !stepTools.Any(tool => tools.TryGetValue(tool, out Records.ToolDefinition? declared) && declared.Parameters.Any(p => p.Name == parameter)))
                errors.Add($"{stepSubject}, \"Arguments\" binds '{parameter}', which is a parameter of none of the step's tools");

            string[] parts = source.Split('.', 2);
            if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0)
            {
                errors.Add($"{stepSubject}, \"Arguments\" binds '{parameter}' to '{source}', which is not of the form Step.field");
                continue;
            }

            Records.WorkflowStep? sourceStep = workflow.Steps.FirstOrDefault(candidate => candidate.Name == parts[0]);
            if (sourceStep is null)
            {
                errors.Add($"{stepSubject}, \"Arguments\" binds '{parameter}' to the step '{parts[0]}', which the workflow does not have");
                continue;
            }

            if (parts[0] == step.Name || !reach.TryGetValue(parts[0], out HashSet<string>? reachable) || !reachable.Contains(step.Name))
            {
                errors.Add($"{stepSubject}, \"Arguments\" binds '{parameter}' to the step '{parts[0]}', which does not run before this one");
                continue;
            }

            foreach (string sourceTool in sourceStep.Tools ?? [])
            {
                bool declaresField = tools.TryGetValue(sourceTool, out Records.ToolDefinition? declared)
                    && declared.Returns?.Any(field => field.Name == parts[1]) == true;

                if (!declaresField)
                    errors.Add($"{stepSubject}, \"Arguments\" binds '{parameter}' to '{source}', but the tool '{sourceTool}' of step '{parts[0]}' does not declare the returned field '{parts[1]}'");
            }
        }
    }

    /// <summary>
    /// The steps that a workflow can reach from one step by following the links of its tools.
    /// </summary>
    private static HashSet<string> ReachableFrom(Records.WorkflowDefinition workflow, string from)
    {
        HashSet<string> reached = new(StringComparer.Ordinal);
        Queue<string> pending = new([from]);

        // The start counts as reached only when a loop leads back to it, which is what "can reach" asks.
        while (pending.TryDequeue(out string? current))
        {
            Records.WorkflowStep? step = workflow.Steps.FirstOrDefault(candidate => candidate.Name == current);
            if (step is null)
                continue;

            foreach (string target in step.Links(false).Values.Concat(step.Links(true).Values))
            {
                if (target != Constants.Workflows.End && reached.Add(target))
                    pending.Enqueue(target);
            }
        }

        return reached;
    }

    /// <summary>
    /// Weighs each declared tool of one intent against the record its method returns.
    /// </summary>
    /// <remarks>
    /// The record is what the model reads and the declaration is what Alembic and a workflow engine read:
    /// the two are one contract only while the same fields stand on both sides.
    /// </remarks>
    /// <param name="intent">The intent that owns the tools, named in the messages.</param>
    /// <param name="toolType">The class implementing the tools.</param>
    /// <param name="declaredTools">The tools that agents.json declares for the intent.</param>
    /// <returns>One message per violation, empty when every contract holds.</returns>
    public static List<string> ValidateToolContract(string intent, Type toolType, IEnumerable<Records.ToolDefinition> declaredTools)
    {
        List<string> errors = [];

        foreach (Records.ToolDefinition tool in declaredTools)
        {
            // A declared tool with no method is the adapter's warning, not a contract violation.
            MethodInfo? method = toolType.GetMethod(tool.Name);
            if (method is null)
                continue;

            string subject = $"Tool '{tool.Name}' of intent '{intent}'";
            Type? returnType = UnwrapReturnType(method.ReturnType);

            // A string or an object tells the schema nothing: the model would read a document that nobody declared.
            if (returnType is null || returnType == typeof(string) || returnType == typeof(object))
            {
                errors.Add($"{subject} returns '{method.ReturnType.Name}': a native tool returns a typed record");
                continue;
            }

            JsonElement schema = MorganaToolAdapter.CreateReturnSchema(returnType);
            if (!schema.TryGetProperty("properties", out JsonElement properties))
            {
                errors.Add($"{subject} returns '{returnType.Name}', which has no properties: a native tool returns a typed record");
                continue;
            }

            if (tool.Returns is null || tool.Returns.Count == 0)
            {
                errors.Add($"{subject} declares no \"Returns\" in agents.json while its method returns '{returnType.Name}'");
                continue;
            }

            HashSet<string> recordFields = [.. properties.EnumerateObject().Select(property => property.Name)];
            HashSet<string> declaredFields = [.. tool.Returns.Select(field => field.Name)];

            foreach (string missing in declaredFields.Except(recordFields, StringComparer.Ordinal))
                errors.Add($"{subject} declares the returned field '{missing}', which '{returnType.Name}' does not have");

            foreach (string undeclared in recordFields.Except(declaredFields, StringComparer.Ordinal))
                errors.Add($"{subject} returns the field '{undeclared}' in '{returnType.Name}', which its \"Returns\" does not declare");

            List<Records.ToolReturn> failureFields = [.. tool.Returns.Where(field => field.Failure)];
            if (failureFields.Count > 1)
                errors.Add($"{subject} marks {failureFields.Count.ToString(CultureInfo.InvariantCulture)} fields as the failure ({string.Join(", ", failureFields.Select(field => $"'{field.Name}'"))}): at most one may be");

            // The failure field is the one whose holding a value means the call failed, so a record that
            // cannot leave it empty would report every call as failed.
            foreach (Records.ToolReturn failure in failureFields.Take(1))
            {
                if (properties.TryGetProperty(failure.Name, out JsonElement failureSchema) && !AllowsNull(failureSchema))
                    errors.Add($"{subject} marks the field '{failure.Name}' as the failure but '{returnType.Name}' does not allow it to be null");
            }
        }

        return errors;
    }

    /// <summary>
    /// Takes the result type out of a <c>Task</c> or a <c>ValueTask</c>; null when the method returns no value.
    /// </summary>
    /// <param name="methodReturnType">The return type as the method declares it.</param>
    private static Type? UnwrapReturnType(Type methodReturnType)
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