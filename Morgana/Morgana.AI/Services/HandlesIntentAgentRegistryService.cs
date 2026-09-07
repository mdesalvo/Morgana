using System.Reflection;
using Microsoft.Extensions.Configuration;
using Morgana.AI.Abstractions;
using Morgana.AI.Adapters;
using Morgana.AI.Attributes;
using Morgana.AI.Interfaces;

namespace Morgana.AI.Services;

/// <summary>
/// Discovers agents via [HandlesIntent] attribute with bidirectional validation.
/// Scans assemblies for MorganaAgent classes; validates: intents in config have agents, agents in code have config,
/// and every declared peer consultation names an existing colleague. Performs LLM tier validation; throws on any mismatch.
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
    /// Registry mapping intent names to agent types.
    /// Built during service initialization via assembly scanning.
    /// Case-insensitive string comparison for intent matching.
    /// </summary>
    private readonly Lazy<Dictionary<string, Type>> intentToAgentType;

    /// <summary>Discovers agents and runs bidirectional intent↔agent validation (lazily — see field above).</summary>
    /// <param name="agentConfigService">Loads intent configuration from agents.json.</param>
    /// <param name="llmTierValidationService">Validates each agent's [RequiresLLMTier], delegated as a separate concern.</param>
    /// <param name="configuration">Application configuration, read for the declared instances.</param>
    /// <exception cref="InvalidOperationException">Validation fails: missing agents or missing configuration.</exception>
    public HandlesIntentAgentRegistryService(
        IAgentConfigurationService agentConfigService,
        ILLMTierValidationService llmTierValidationService,
        IConfiguration configuration)
    {
        this.agentConfigService = agentConfigService;
        this.llmTierValidationService = llmTierValidationService;
        this.configuration = configuration;

        // The scan must see every plugin assembly. DI construction order does not guarantee they have
        // all loaded, so the registry is built on first use rather than here.
        intentToAgentType = new Lazy<Dictionary<string, Type>>(InitializeRegistry);
    }

    /// <summary>
    /// Scans every loaded assembly for <see cref="MorganaAgent"/> subclasses declaring an intent,
    /// and returns the intent-to-type map, without validating it.
    /// </summary>
    /// <returns>Intent to agent type, case-insensitive; agents without <c>[HandlesIntent]</c> are skipped.</returns>
    public static Dictionary<string, Type> DiscoverAgents()
    {
        // The roster of desks this installation answers with, one per intent. The two spellings that
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
            // desks, never a desk that answers.
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
            .. ValidatePeerDeclarations(registry)
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

        // Every configured intent is a modelled desk and is owed an agent. The one intent that is
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