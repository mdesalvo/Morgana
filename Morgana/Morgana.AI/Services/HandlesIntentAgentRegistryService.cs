using System.ComponentModel;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Morgana.AI.Abstractions;
using Morgana.AI.Adapters;
using Morgana.AI.Attributes;
using Morgana.AI.Interfaces;
using Morgana.AI.Tools;

namespace Morgana.AI.Services;

/// <summary>
/// Finds the agent of each intent by scanning the loaded assemblies for <c>[HandlesIntent]</c>, refusing at startup a topology that does not hold.
/// It weighs the configured intents against the agents both ways, one agent per intent, the declared tiers, the colleagues and the tool contracts.
/// </summary>
public class HandlesIntentAgentRegistryService : IAgentRegistryService
{
    /// <summary>The configured intents, each of which must be matched by a <c>[HandlesIntent]</c> agent and the other way round.</summary>
    private readonly IAgentConfigurationService agentConfigService;

    /// <summary>Source of the tool classes, whose tools and parameters the startup check weighs.</summary>
    private readonly IToolRegistryService toolRegistryService;

    /// <summary>
    /// The configuration, read for the partners a consultation may name.
    /// A colleague published elsewhere is declared there and not in code, so the startup check has nowhere else to learn of it.
    /// </summary>
    private readonly IConfiguration configuration;

    /// <summary>The agent type of each intent, scanned and validated on first use and looked up ignoring case.</summary>
    private readonly Lazy<Dictionary<string, Type>> intentToAgentType;

    /// <summary>Defers the scan and its validation until the first lookup, which the host makes at startup.</summary>
    public HandlesIntentAgentRegistryService(
        IAgentConfigurationService agentConfigService,
        IToolRegistryService toolRegistryService,
        IConfiguration configuration)
    {
        this.agentConfigService = agentConfigService;
        this.toolRegistryService = toolRegistryService;
        this.configuration = configuration;

        // The scan must see every plugin assembly. DI construction order does not guarantee they have
        // all loaded, so the registry is built on first use rather than here.
        intentToAgentType = new Lazy<Dictionary<string, Type>>(InitializeRegistry);
    }

    /// <summary>Returns the agent type of each intent as the scan finds it, without validating it.</summary>
    public static Dictionary<string, Type> DiscoverAgents()
    {
        // The roster of agents this installation answers with, one per intent. The two spellings that
        // must meet here are typed by hand in different files, so casing is not allowed to part them.
        Dictionary<string, Type> registry = new(StringComparer.OrdinalIgnoreCase);

        // An intent claimed by two agents keeps the last one found here; the validation refuses it before any conversation runs.
        foreach ((string intent, Type agentType) in FindIntentAgents())
            registry[intent] = agentType;

        // Unvalidated on purpose: the host reads this before the container exists, to publish one A2A
        // endpoint per agent, where throwing would refuse a deployment over a check it has not run yet.
        return registry;
    }

    /// <summary>Finds every public concrete <see cref="MorganaAgent"/> of the loaded assemblies that declares an intent, with that intent.</summary>
    private static List<(string Intent, Type AgentType)> FindIntentAgents()
    {
        // Every assembly in the process, since a domain arrives as a plugin DLL loaded before this runs.
        IEnumerable<Type> morganaAgentTypes = AppDomain.CurrentDomain.GetAssemblies()
            // A runtime-generated assembly holds no agent an author wrote.
            .Where(assembly => !assembly.IsDynamic)
            .SelectMany(assembly =>
            {
                try
                {
                    return assembly.GetTypes();
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
            .Where(type => type is { IsClass: true, IsAbstract: false } && type.IsSubclassOf(typeof(MorganaAgent)))
            // Published agents only: a plugin offers its agents as public classes.
            .Where(type => type.IsVisible);

        // An agent without [HandlesIntent] answers for nothing and is passed over in silence.
        return
        [
            .. from morganaAgentType in morganaAgentTypes
               let handlesIntentAttribute = morganaAgentType.GetCustomAttribute<HandlesIntentAttribute>()
               where handlesIntentAttribute != null
               select (handlesIntentAttribute.Intent, morganaAgentType)
        ];
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
            .. ValidateUniqueAgents(),
            .. ValidateDeclaredTiers(registry),
            .. ValidatePeerDeclarations(registry),
            .. ValidateToolContracts()
        ];

        // One exception carrying every problem, so a deployment is fixed in one pass.
        if (validationErrors.Count > 0)
            throw new InvalidOperationException(string.Join(Environment.NewLine, validationErrors));

        // The roster that every routing reads from here on.
        return registry;
    }

    /// <summary>Refuses an intent that two agents claim, since which one answers would be left to the order of reflection.</summary>
    /// <returns>One message per intent claimed twice, empty when each intent has one agent.</returns>
    private static List<string> ValidateUniqueAgents()
    {
        // The scan's own list, before the roster keeps one agent per intent.
        return
        [
            .. from claims in FindIntentAgents().GroupBy(intentAgent => intentAgent.Intent, StringComparer.OrdinalIgnoreCase)
               where claims.Count() > 1
               select $"The intent '{claims.Key}' is handled by more than one Morgana agent: {string.Join(", ", claims.Select(claim => claim.AgentType.Name))}"
        ];
    }

    /// <summary>
    /// Weighs the configured intents against the agents that declare them, in both directions.
    /// An intent nobody handles reaches the classifier then routes nowhere; an agent nobody declared is never reached at all.
    /// </summary>
    /// <param name="registry">The discovered intent-to-type map.</param>
    /// <param name="configuredIntents">The intents declared in <c>agents.json</c>.</param>
    /// <returns>One message per direction that does not hold, empty when both do.</returns>
    private static List<string> ValidateIntentCoverage(
        Dictionary<string, Type> registry,
        List<Records.IntentDefinition> configuredIntents)
    {
        // One message per direction that does not hold.
        List<string> errors = [];
        HashSet<string> registeredIntents = [.. registry.Keys];

        // Every configured intent is a modelled agent and is owed an agent. The one intent that is
        // not — the complement of the domain — never reaches here: it belongs to the classifier and
        // is described in its prompt, so no domain declares it and none has to be excused for it.
        HashSet<string> classifierIntents = [.. configuredIntents.Select(intent => intent.Name)];

        // Offered to the classifier with nobody behind it: the router would answer with Morgana's
        // presentation a request the domain claims to serve.
        List<string> unhandledIntents = [.. classifierIntents.Except(registeredIntents)];
        if (unhandledIntents.Count > 0)
            errors.Add($"There are intents not handled by any Morgana agent: {string.Join(", ", unhandledIntents)}");

        // Written in code with nothing routing to it: the classifier has never heard that name, so the
        // agent is unreachable however correct it is.
        List<string> undeclaredIntents = [.. registeredIntents.Except(classifierIntents)];
        if (undeclaredIntents.Count > 0)
            errors.Add($"There are Morgana agents handling an undeclared intent: {string.Join(", ", undeclaredIntents)}");

        // Both directions, joined by the caller with the other checks.
        return errors;
    }

    /// <summary>
    /// Refuses an agent that does not declare the tier it runs on.
    /// Every declared tier is served, since the three tiers are all mandatory in configuration.
    /// </summary>
    /// <param name="registry">The discovered intent-to-type map.</param>
    /// <returns>One message listing the agents without <c>[RequiresLLMTier]</c>, empty when every agent declares one.</returns>
    private static List<string> ValidateDeclaredTiers(Dictionary<string, Type> registry)
    {
        // Undeclared, so nobody chose. Defaulting would put an agent on a model its author never
        // weighed, which is the silent wrong-model failure this check exists to prevent.
        List<string> missingAttribute =
        [
            .. registry
                .Where(entry => entry.Value.GetCustomAttribute<RequiresLLMTierAttribute>() is null)
                .Select(entry => $"{entry.Value.Name} (intent '{entry.Key}')")
        ];

        // One message naming every offender, empty when each agent has chosen its tier.
        return missingAttribute.Count > 0
            ? [$"The following Morgana agents are missing the mandatory [RequiresLLMTier] attribute: {string.Join(", ", missingAttribute)}"]
            : [];
    }

    /// <summary>
    /// Refuses a <c>[ConsultsAgent]</c> declaration naming a colleague that cannot be reached.
    /// </summary>
    /// <param name="registry">The discovered map, which is also the roster of colleagues of this installation.</param>
    /// <returns>One message per declaration that does not hold, empty when all of them do.</returns>
    private List<string> ValidatePeerDeclarations(Dictionary<string, Type> registry)
    {
        // One message per declaration that does not hold, across every agent.
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
                    // An agent consulting itself would answer its own question in a loop.
                    if (string.Equals(consultsAgent.Intent, declaredIntent, StringComparison.OrdinalIgnoreCase))
                        errors.Add($"Agent '{agentType.Name}' declares a consultation of itself ('{declaredIntent}')");
                    // A colleague no agent handles could never answer.
                    else if (!registry.ContainsKey(consultsAgent.Intent))
                        errors.Add($"Agent '{agentType.Name}' declares a consultation of '{consultsAgent.Intent}', which no Morgana agent handles");
                }

                // One published elsewhere: only this side of the wire is checkable.
                else if (ValidateConsultablePartner(partners, consultsAgent.Instance, agentType.Name, colleague) is { } partnerError)
                {
                    errors.Add(partnerError);
                }

                // Two colleagues folding to one function name would reach the provider as a duplicate
                // tool, which it refuses outright: on the first conversation, not here.
                if (!declaredColleagues.Add(peerFunctionName))
                    errors.Add($"Agent '{agentType.Name}' declares a consultation of {colleague} under a name it already offers another colleague under ('{peerFunctionName}')");
            }
        }

        // Every agent's faults, joined by the caller with the other checks.
        return errors;
    }

    /// <summary>Refuses a tool class whose tools or parameters are not declared completely.</summary>
    /// <returns>One message per violation, empty when every tool class holds.</returns>
    private List<string> ValidateToolContracts()
    {
        // Every tool class is weighed before anything is thrown, so one boot reports all of their faults.
        List<string> errors = [];

        // The names a domain tool may not take: the base tools sit ahead of every agent's own.
        HashSet<string> baseToolNames = new HashSet<string>(
            MorganaToolAdapter.GetToolMethods(typeof(MorganaTool)).Select(method => method.Name),
            StringComparer.OrdinalIgnoreCase);

        // The base tools are weighed like any tool class: a tool lost there is a tool no agent can call.
        ValidateToolClass(typeof(MorganaTool), "the base tools", baseToolNames, false, errors);

        // Every registered tool class is weighed under the intent it serves.
        foreach ((string toolIntent, Type toolType) in toolRegistryService.GetAllRegisteredTools())
            ValidateToolClass(toolType, $"intent '{toolIntent}'", baseToolNames, true, errors);

        // The faults of every tool class, joined by the caller with those of the agents.
        return errors;
    }

    /// <summary>Adds one message per way a tool class fails to declare its tools completely.</summary>
    /// <param name="toolType">The tool class to weigh.</param>
    /// <param name="owner">Who the class serves as the messages name it.</param>
    /// <param name="baseToolNames">The names of the base tools.</param>
    /// <param name="isDomainClass">Whether the class is a domain one whose tools may not take a base tool's name.</param>
    /// <param name="errors">Receives the messages.</param>
    private static void ValidateToolClass(Type toolType, string owner, HashSet<string> baseToolNames, bool isDomainClass, List<string> errors)
    {
        // The same methods the agent adapter projects, so what is weighed here is what the model reads.
        IReadOnlyList<MethodInfo> toolMethods = MorganaToolAdapter.GetToolMethods(toolType);

        // The model addresses a tool by its name: two methods folding to one name would reach the provider as a duplicate.
        errors.AddRange(
            from sameNameMethods
            in toolMethods.GroupBy(method => method.Name, StringComparer.OrdinalIgnoreCase)
            where sameNameMethods.Count() > 1
            select $"Tool '{sameNameMethods.First().Name}' of {owner} is declared by more than one method of '{toolType.Name}': a tool name is unique");

        // Each tool is weighed on its own, its parameters included.
        foreach (MethodInfo toolMethod in toolMethods)
        {
            // The description is what the model reads to know what the tool does.
            if (string.IsNullOrWhiteSpace(toolMethod.GetCustomAttribute<DescriptionAttribute>()?.Description))
                errors.Add($"Tool '{toolMethod.Name}' of {owner} has no [Description]: the model reads it to know what the tool does");

            // A domain tool named like a base tool would shadow it in the list the model reads.
            if (isDomainClass && baseToolNames.Contains(toolMethod.Name))
                errors.Add($"Tool '{toolMethod.Name}' of {owner} takes the name of a base tool");

            // Each parameter reaches the model through the tool's schema, so each is weighed too.
            foreach (ParameterInfo parameter in toolMethod.GetParameters())
            {
                // The description is what the model reads to know what to pass.
                if (string.IsNullOrWhiteSpace(parameter.GetCustomAttribute<DescriptionAttribute>()?.Description))
                    errors.Add($"Parameter '{parameter.Name}' of tool '{toolMethod.Name}' of {owner} has no [Description]: the model reads it to know what to pass");

                // A parameter without [ToolParameter] holds a value the model authors: it has no scope to weigh.
                ToolParameterAttribute? parameterDeclaration = parameter.GetCustomAttribute<ToolParameterAttribute>();

                // The context holds text only, so any other type could never be read back.
                if (parameterDeclaration?.Scope is Records.ToolScope.Context or Records.ToolScope.SharedContext && parameter.ParameterType != typeof(string))
                    errors.Add($"Parameter '{parameter.Name}' of tool '{toolMethod.Name}' of {owner} is a context parameter of type '{parameter.ParameterType.Name}': the context holds text, so it is a string");
            }
        }
    }

    /// <summary>
    /// Checks that the partner an attribute names is one this installation may actually call.
    /// What that partner publishes is its card's word, read on the first consultation; its address and key are checked with the rest of the entry.
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

        // The partner is declared and open to being called.
        return null;
    }

    /// <summary>Returns the agent type of an intent, ignoring case; null for Other and for an unknown name, which Morgana answers herself.</summary>
    public Type? ResolveAgentFromIntent(string intent)
        // The first lookup runs the scan and its validation.
        => intentToAgentType.Value.GetValueOrDefault(intent);

    /// <summary>Every intent that an agent handles: the host asks for them at startup and the router names the agents it stops by them.</summary>
    public IEnumerable<string> GetAllIntents()
        // The first lookup runs the scan and its validation.
        => intentToAgentType.Value.Keys;
}