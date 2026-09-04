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
    /// intent it declares must be matched by a <c>[HandlesIntent]</c> agent, and vice versa.
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

        // Lazy rather than run in the constructor: InitializeRegistry does a full-AppDomain
        // reflection scan plus the startup-fatal bidirectional validation below, and both need to
        // happen after every plugin assembly has finished loading — deferring to first use (which
        // in practice is startup validation itself, just called explicitly rather than via DI
        // construction order) keeps this service indifferent to exactly when it gets constructed.
        intentToAgentType = new Lazy<Dictionary<string, Type>>(InitializeRegistry);
    }

    /// <summary>
    /// Whether any discovered agent declares a colleague of this same installation.
    /// </summary>
    /// <remarks>
    /// The predicate the "morgana" issuer hangs on: only a LOCAL consultation is signed with this
    /// installation's own key and comes back in through its own A2A door.
    /// </remarks>
    /// <param name="discoveredAgents">The intent-to-type map <see cref="DiscoverAgents"/> returned.</param>
    /// <returns><c>true</c> when at least one agent declares <c>[ConsultsAgent]</c> naming no system.</returns>
    public static bool DeclaresLocalConsultations(IReadOnlyDictionary<string, Type> discoveredAgents)
        => discoveredAgents.Values.Any(agentType =>
               agentType.GetCustomAttributes<ConsultsAgentAttribute>()
                        .Any(consultsAgent => consultsAgent.Instance is null));

    /// <summary>
    /// Scans every loaded assembly for <see cref="MorganaAgent"/> subclasses declaring an intent,
    /// and returns the intent-to-type map, without validating it.
    /// </summary>
    /// <remarks>
    /// Static and validation-free because the host needs the same map before the DI container is
    /// built — to publish one A2A endpoint per agent — while this service needs it after, with the
    /// startup checks applied. One implementation for both, rather than two that can drift.
    /// </remarks>
    /// <returns>Intent to agent type, case-insensitive; agents without <c>[HandlesIntent]</c> are skipped.</returns>
    public static Dictionary<string, Type> DiscoverAgents()
    {
        Dictionary<string, Type> registry = new(StringComparer.OrdinalIgnoreCase);

        IEnumerable<Type> morganaAgentTypes = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic)
            .SelectMany(a =>
            {
                try
                {
                    return a.GetTypes();
                }
                catch (ReflectionTypeLoadException)
                {
                    // Gracefully handle assemblies that fail to load completely
                    return [];
                }
            })
            .Where(t => t is { IsClass: true, IsAbstract: false } && t.IsSubclassOf(typeof(MorganaAgent)));

        foreach (Type? morganaAgentType in morganaAgentTypes)
        {
            // No [HandlesIntent] at all is a silent skip (e.g. an abstract base agent). Two
            // DIFFERENT agents declaring the SAME intent is a real bug this indexer hides: the
            // later one in (undocumented) reflection order silently shadows the earlier one.
            HandlesIntentAttribute? handlesIntentAttribute = morganaAgentType.GetCustomAttribute<HandlesIntentAttribute>();
            if (handlesIntentAttribute != null)
                registry[handlesIntentAttribute.Intent] = morganaAgentType;
        }

        return registry;
    }

    /// <summary>
    /// Discovers the agents, then validates the bidirectional intent↔agent mapping.
    /// Collects all validation errors before throwing, so a misconfigured deployment reports them at once.
    /// </summary>
    /// <returns>Dictionary mapping intent names to agent types</returns>
    /// <exception cref="InvalidOperationException">If validation fails (missing agents or configs)</exception>
    private Dictionary<string, Type> InitializeRegistry()
    {
        Dictionary<string, Type> registry = DiscoverAgents();

        #region Validation
        // Bidirectional validation of Morgana agents and intents
        // Load intents from domain-specific configuration
        List<Records.IntentDefinition> allIntents = agentConfigService.GetIntentsAsync().GetAwaiter().GetResult();

        // Extract intent names, excluding Intents.Other (the fallback intent, by design agentless)
        HashSet<string> classifierIntents =
        [
            .. allIntents
                .Where(intent => !string.Equals(intent.Name, Constants.Intents.Other, StringComparison.OrdinalIgnoreCase))
                .Select(intent => intent.Name)
        ];

        HashSet<string> registeredIntents = [.. registry.Keys];

        // All three checks run and are collected before anything is thrown, so a
        // misconfigured deployment reports every problem it has at once instead of one
        // category per restart cycle (intent mismatch, then tier mismatch, then...).
        List<string> validationErrors = [];

        // Check 1: Configured intents without agent implementations
        List<string> unregisteredClassifierIntents = [.. classifierIntents.Except(registeredIntents)];
        if (unregisteredClassifierIntents.Count > 0)
            validationErrors.Add(
                $"There are intents not handled by any Morgana agent: {string.Join(", ", unregisteredClassifierIntents)}");

        // Check 2: Agent implementations without configuration entries
        List<string> unconfiguredAgentIntents = [.. registeredIntents.Except(classifierIntents)];
        if (unconfiguredAgentIntents.Count > 0)
            validationErrors.Add(
                $"There are Morgana agents handling an undeclared intent: {string.Join(", ", unconfiguredAgentIntents)}");

        // Check 3: every agent must declare [RequiresLLMTier], and the declared tier must
        // actually be configured (a Tiers entry) for the active LLM provider. Delegated to
        // ILLMTierValidationService — a separate concern (LLM cost/tier governance) from
        // intent↔agent discovery, kept as its own extension point rather than inlined here.
        // It already aggregates every offending agent into its own message, so on failure
        // that single message is folded into the overall list below rather than thrown here.
        try
        {
            llmTierValidationService.ValidateAgentTiers(registry);
        }
        catch (InvalidOperationException ex)
        {
            validationErrors.Add(ex.Message);
        }

        // Check 4: every [ConsultsAgent] declaration must name a colleague that can actually be
        // reached — an agent of this installation, or an intent at a declared system — never the
        // declaring agent itself, and never the same colleague twice. A consultation is resolved at
        // agent-creation time, long after startup, so each of these would otherwise surface on the
        // first conversation: a typo as a colleague that silently never appears, a duplicate as two
        // functions of the same name in the tool list, which the provider rejects outright.
        List<Records.OutboundSystemOptions> outboundSystems = ConfigurationAgentDirectoryService.ResolveOutboundSystems(configuration);

        foreach ((string declaredIntent, Type agentType) in registry)
        {
            HashSet<string> declaredColleagues = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (ConsultsAgentAttribute consultsAgent in agentType.GetCustomAttributes<ConsultsAgentAttribute>())
            {
                // What must be unique is the name the colleague is offered under, not the pair that
                // produced it: systems are named by people ("Newco Finance"), and a function name is
                // constrained where a system name is not — so two distinct declarations can sanitize
                // to one function, which the provider rejects outright. Derived by the very method the
                // adapter will use, so the check and the tool list cannot disagree.
                string peerFunctionName = MorganaAgentAdapter.ToFunctionName(
                    new Records.PeerReference(consultsAgent.Intent, consultsAgent.Instance));

                string colleague = consultsAgent.Instance is null
                    ? $"'{consultsAgent.Intent}'"
                    : $"'{consultsAgent.Intent}' at system '{consultsAgent.Instance}'";

                if (consultsAgent.Instance is null)
                {
                    if (string.Equals(consultsAgent.Intent, declaredIntent, StringComparison.OrdinalIgnoreCase))
                        validationErrors.Add($"Agent '{agentType.Name}' declares a consultation of itself ('{declaredIntent}')");
                    else if (!registry.ContainsKey(consultsAgent.Intent))
                        validationErrors.Add($"Agent '{agentType.Name}' declares a consultation of '{consultsAgent.Intent}', which no Morgana agent handles");
                }
                else
                {
                    // What a system publishes cannot be verified from here — that is the point of a
                    // card, and it is fetched on the first consultation. What can be verified is that
                    // the declaration names something addressable and signable, which is the whole of
                    // what this side must bring.
                    Records.OutboundSystemOptions? outboundSystem = outboundSystems
                        .FirstOrDefault(candidate => string.Equals(candidate.Name.Trim(), consultsAgent.Instance, StringComparison.OrdinalIgnoreCase));

                    if (outboundSystem is null)
                        validationErrors.Add(
                            $"Agent '{agentType.Name}' declares a consultation of {colleague}, which is not declared under Morgana:AgentToAgent:OutboundSystems "
                            + $"(declared: {(outboundSystems.Count > 0 ? string.Join(", ", outboundSystems.Select(declared => $"'{declared.Name}'")) : "none")}). "
                            + "The name on the attribute and the Name on the entry must be the same, spelling and spacing included");
                    else if (!Uri.TryCreate(outboundSystem.Url, UriKind.Absolute, out _))
                        validationErrors.Add($"System '{outboundSystem.Name}', consulted by agent '{agentType.Name}', declares no absolute Url");
                    else if (string.IsNullOrWhiteSpace(outboundSystem.SymmetricKey)
                             || string.Equals(outboundSystem.SymmetricKey.Trim(), Constants.Overrides.Secure, StringComparison.Ordinal))
                        validationErrors.Add($"System '{outboundSystem.Name}', consulted by agent '{agentType.Name}', carries no usable SymmetricKey (User Secrets or environment)");
                }

                if (!declaredColleagues.Add(peerFunctionName))
                    validationErrors.Add($"Agent '{agentType.Name}' declares a consultation of {colleague} under a name it already offers another colleague under ('{peerFunctionName}')");
            }
        }

        if (validationErrors.Count > 0)
            throw new InvalidOperationException(string.Join(Environment.NewLine, validationErrors));
        #endregion

        return registry;
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