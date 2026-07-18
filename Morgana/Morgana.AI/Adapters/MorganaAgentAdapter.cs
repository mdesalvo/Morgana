using System.Reflection;
using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Morgana.AI.Abstractions;
using Morgana.AI.Attributes;
using Morgana.AI.Interfaces;
using Morgana.AI.Providers;
using Morgana.AI.Services;

namespace Morgana.AI.Adapters;

// This suppresses the experimental API warning for IChatReducer usage.
// Microsoft marks IChatReducer as experimental (MEAI001) but recommends it
// for production use in context window management scenarios.
#pragma warning disable MEAI001 // Type is for evaluation purposes only and is subject to change or removal in future updates

/// <summary>
/// Creates and configures <see cref="AIAgent"/> instances from Morgana agent definitions.
/// Handles instruction composition, tool registration, provider setup and MCP integration.
/// </summary>
/// <remarks>
/// <para><strong>Session accessor pattern:</strong> <see cref="MorganaAIContextProvider"/> is a singleton
/// and all context state lives in <see cref="AgentSession"/>. Every provider call (GetVariable, SetVariable,
/// DropVariable) requires the active session. Tools receive both provider and session via a
/// <c>Func&lt;MorganaTool.ToolContext&gt;</c> factory evaluated lazily at tool-call time.</para>
///
/// <para><see cref="MorganaAgentAdapter"/> does not hold a reference to the Akka agent actor, so the
/// active session is supplied by the concrete agent via a <c>Func&lt;AgentSession?&gt; sessionAccessor</c>
/// parameter on <see cref="CreateAgent"/>. The accessor captures <see cref="MorganaAgent.CurrentSession"/>,
/// which is always non-null during tool execution (Akka single-thread guarantee).</para>
/// </remarks>
public class MorganaAgentAdapter
{
    /// <summary>
    /// Service for resolving prompt templates from configuration sources (morgana.json, agents.json).
    /// </summary>
    protected readonly IPromptResolverService promptResolverService;

    /// <summary>
    /// LLM service abstraction, queried per-agent for the chat client and dust pricing of the
    /// tier its <c>[RequiresLLMTier]</c> attribute declares. There is no single process-wide
    /// chat client here anymore — each agent resolves its own tier at creation time.
    /// </summary>
    protected readonly ILLMService llmService;

    /// <summary>
    /// Service for discovering custom MorganaTool implementations via [ProvidesToolForIntent] attribute.
    /// Returns null if no custom tool exists for an intent (MCP-only agents).
    /// </summary>
    protected readonly IToolRegistryService toolRegistryService;

    /// <summary>
    /// Service for managing MCP (Model Context Protocol) client connections and lifecycle.
    /// Provides connection pooling and tool discovery from external MCP servers.
    /// </summary>
    protected readonly IMCPClientRegistryService imcpClientRegistryService;

    /// <summary>
    /// Service for creating IChatReducer instances for context window management.
    /// Creates SummarizingChatReducer based on configuration to optimize LLM costs.
    /// </summary>
    protected readonly SummarizingChatReducerService chatReducerService;

    /// <summary>
    /// Per-conversation lifetime token-budget limiter. Domain-agent LLM calls (and their
    /// history reducer) are metered through it under a per-agent role.
    /// </summary>
    protected readonly IDustLimitService dustLimitService;

    /// <summary>
    /// Logger instance for agent creation diagnostics and tool registration tracking.
    /// </summary>
    protected readonly ILogger logger;

    /// <summary>
    /// Morgana framework prompt containing global policies, base tools, and error message templates.
    /// Loaded once during adapter initialization from morgana.json.
    /// </summary>
    protected readonly Records.Prompt morganaPrompt;

    /// <summary>
    /// Initializes a new instance of the MorganaAgentAdapter.
    /// Loads the Morgana framework prompt for later composition with domain prompts.
    /// </summary>
    /// <param name="llmService">LLM service abstraction, queried per-agent for its declared tier's chat client and pricing</param>
    /// <param name="promptResolverService">Service for resolving prompt templates</param>
    /// <param name="toolRegistryService">Service for discovering custom MorganaTool implementations</param>
    /// <param name="imcpClientRegistryService">Service for managing MCP server connections</param>
    /// <param name="chatReducerService">Service for reducing context window sent to LLM</param>
    /// <param name="dustLimitService">Per-conversation lifetime token-budget limiter</param>
    /// <param name="logger">Logger instance for diagnostics</param>
    public MorganaAgentAdapter(
        ILLMService llmService,
        IPromptResolverService promptResolverService,
        IToolRegistryService toolRegistryService,
        IMCPClientRegistryService imcpClientRegistryService,
        SummarizingChatReducerService chatReducerService,
        IDustLimitService dustLimitService,
        ILogger logger)
    {
        this.llmService = llmService;
        this.promptResolverService = promptResolverService;
        this.toolRegistryService = toolRegistryService;
        this.imcpClientRegistryService = imcpClientRegistryService;
        this.chatReducerService = chatReducerService;
        this.dustLimitService = dustLimitService;
        this.logger = logger;

        morganaPrompt = promptResolverService.ResolveAsync("Morgana").GetAwaiter().GetResult();
    }

    /// <summary>
    /// Creates a fully configured <see cref="AIAgent"/> instance for the given agent type.
    /// </summary>
    /// <param name="agentType">
    /// Agent class decorated with <c>[HandlesIntent]</c>.
    /// </param>
    /// <param name="conversationId">
    /// Identifier of the ongoing conversation.
    /// </param>
    /// <param name="sessionAccessor">
    /// Returns the agent's current <see cref="AgentSession"/> at tool-call time.
    /// Wire as <c>() =&gt; CurrentSession</c> from the concrete <see cref="MorganaAgent"/> subclass.
    /// May return <c>null</c> at construction time; guaranteed non-null during actual tool execution.
    /// </param>
    /// <param name="sharedContextCallback">
    /// Optional callback invoked when the agent writes a shared context variable. Wire to
    /// <see cref="MorganaAgent.OnSharedContextUpdate"/>, which persists the value into the
    /// conversation-scoped <c>shared_context</c> registry so other agents pick it up at the
    /// start of their next turn.
    /// </param>
    /// <returns>
    /// A tuple of (AIAgent, MorganaAIContextProvider, MorganaChatHistoryProvider) —
    /// all three singletons for this agent instance.
    /// </returns>
    public (AIAgent agent, MorganaAIContextProvider contextProvider, MorganaChatHistoryProvider historyProvider) CreateAgent(
        Type agentType,
        string conversationId,
        Func<AgentSession?> sessionAccessor,
        Action<string, object>? sharedContextCallback = null)
    {
        // 1) Identity: the [HandlesIntent] attribute is the agent's contract. Its absence
        //    is a wiring bug (a MorganaAgent subclass that forgot the attribute), so fail
        //    loud at creation rather than silently producing an unroutable agent.
        HandlesIntentAttribute? intentAttribute = agentType.GetCustomAttribute<HandlesIntentAttribute>()
            ?? throw new InvalidOperationException($"Agent type '{agentType.Name}' must be decorated with [HandlesIntent] attribute");

        // 1b) Tier: the agent's fixed, "existential" declaration of which model class it runs
        //     on. Mandatory alongside [HandlesIntent] — see RequiresLLMTierAttribute remarks.
        //     Startup validation (HandlesIntentAgentRegistryService) already guarantees this
        //     attribute is present and its tier is configured for the active provider before
        //     any agent is ever created, so both lookups below are safe.
        RequiresLLMTierAttribute tierAttribute = agentType.GetCustomAttribute<RequiresLLMTierAttribute>()
            ?? throw new InvalidOperationException($"Agent type '{agentType.Name}' must be decorated with [RequiresLLMTier] attribute");

        logger.LogInformation("Creating agent for intent '{IntentAttributeIntent}' on tier '{Tier}'...", intentAttribute.Intent, tierAttribute.Tier);

        // 2) Domain prompt for this intent (instructions/personality/formatting/tools),
        //    resolved from agents.json. Sync-over-async is intentional: agent creation is
        //    a one-time, non-hot setup path.
        Records.Prompt agentPrompt = promptResolverService.ResolveAsync(intentAttribute.Intent).GetAwaiter().GetResult();

        // 3) Tool surface = framework base tools (morgana.json: GetContextVariable,
        //    SetContextVariable, SetQuickReplies, SetRichCard) UNION the agent's domain
        //    tools (agents.json). Union de-dups so a domain tool can't shadow a base one.
        Records.ToolDefinition[] agentTools = [.. morganaPrompt.GetAdditionalProperty<Records.ToolDefinition[]>("Tools")
                                                    .Union(agentPrompt.GetAdditionalProperty<Records.ToolDefinition[]>("Tools"))];

        // 4) Per-agent context provider (the variable store behind GetVariable/SetVariable);
        //    sharedContextCallback wires Shared:true writes into the cross-agent registry.
        MorganaAIContextProvider morganaAIContextProvider = CreateAIContextProvider(
            intentAttribute.Intent,
            agentTools,
            sharedContextCallback);

        // 5) ToolContext factory — evaluated lazily on EACH tool call, never now. The
        //    adapter holds no actor reference, so the session is pulled fresh via
        //    sessionAccessor at call time (Akka's single-thread guarantee makes it
        //    non-null during execution). A null here means the agent was invoked without
        //    ExecuteAgentAsync seeding the session — a hard wiring error, so throw.
        Func<MorganaTool.ToolContext> toolContextFactory = () =>
        {
            AgentSession session = sessionAccessor()
                ?? throw new InvalidOperationException(
                    $"Agent '{intentAttribute.Intent}' has no active session during tool execution. " +
                    $"Ensure ExecuteAgentAsync sets aiAgentSession before invoking the agent.");

            return new MorganaTool.ToolContext(morganaAIContextProvider, session, conversationId);
        };

        // 6a) Bind the declared tools to their delegates (native MorganaTool methods), then
        //    layer on any [UsesMCPServer] tools discovered from external MCP servers.
        MorganaToolAdapter morganaToolAdapter = CreateToolAdapterForIntent(
            intentAttribute.Intent,
            agentTools,
            toolContextFactory);

        // 6b) Layer on tools from every [UsesMCPServer] on the agent: each server is
        //     discovered through the reconnect-safe path and its tools registered into the
        //     same adapter. Best-effort by design — a server that is down or misconfigured
        //     is logged per-server and skipped, never aborting agent creation (an
        //     MCP-only agent simply ends up with no tools rather than failing to exist).
        RegisterMCPTools(agentType, morganaToolAdapter);

        // 7) Resolve THIS agent's own tier client/pricing (never the framework-default
        //    client) and wrap it in a per-agent dust meter. The role label
        //    ("Morgana (Billing/Efficiency)" etc.) attributes consumption to this agent+tier in
        //    the budget; conversationId scopes the charge. The reducer is built on the SAME
        //    wrapped client so its summarization LLM calls (also token-bearing) are
        //    metered too, not silently free.
        string intent = intentAttribute.Intent;
        // Builds a human-readable label for the dust ledger and OTel tags, e.g. "billing" ->
        // "Morgana (Billing/Efficiency)".
        string dustRole = $"Morgana ({char.ToUpperInvariant(intent[0])}{intent[1..]}/{tierAttribute.Tier})";
        IChatClient tierChatClient = llmService.GetChatClient(tierAttribute.Tier);
        Records.MagicDustPricing tierPricing = llmService.GetPricing(tierAttribute.Tier);
        IChatClient agentChatClient =
            new DustAccountingChatClient(tierChatClient, dustLimitService, tierPricing, dustRole, conversationId);

        // 8) History provider: keeps the full transcript in AgentSession, exposes the
        //    (optionally reduced) view to the LLM. Null reducer → full history verbatim.
        IChatReducer? chatReducer = chatReducerService.CreateReducer(agentChatClient);
        MorganaChatHistoryProvider chatHistoryProvider = new MorganaChatHistoryProvider(intentAttribute.Intent, chatReducer, logger);

        // 9) Assemble the Microsoft.Agents.AI agent over the metered client, injecting the
        //    context + history providers, a stable per-conversation Id (intent-conversationId),
        //    the two-layer composed instructions (framework prompt + domain prompt), and the
        //    tool delegates materialized as AIFunctions.
        AIAgent aiAgent = agentChatClient.AsAIAgent(
            new ChatClientAgentOptions
            {
                // Give the agent its context providers
                AIContextProviders = [morganaAIContextProvider],

                // Give the agent its history provider
                ChatHistoryProvider = chatHistoryProvider,

                // Give the agent its identifiers
                Id = $"{intentAttribute.Intent.ToLower()}-{conversationId}",
                Name = intentAttribute.Intent,

                // Give the agent its instructions and tools
                ChatOptions = new ChatOptions
                {
                    Instructions = ComposeAgentInstructions(agentPrompt),
                    Tools = [.. morganaToolAdapter.CreateAllFunctions()]
                }
            });

        // 10) Return all three: the caller (MorganaAgent subclass) keeps the provider and
        //     history-provider handles to drive context/history across turns — the agent
        //     alone is not enough because providers are queried/mutated outside InvokeAsync.
        return (aiAgent, morganaAIContextProvider, chatHistoryProvider);
    }

    private string FormatGlobalPolicies(List<Records.GlobalPolicy> policies)
    {
        StringBuilder sb = new StringBuilder();

        // Critical before Operational (Type), then ascending Priority within each type.
        // The LLM reads the system prompt top-to-bottom, so P0 Critical constraints must
        // appear before P0 Operational guidance — the order is load-bearing for compliance.
        foreach (Records.GlobalPolicy policy in policies.OrderBy(p => p.Type)
                                                        .ThenBy(p => p.Priority))
        {
            sb.AppendLine($"{policy.Name}: {policy.Description}");
        }

        return sb.ToString().TrimEnd();
    }

    private string ComposeAgentInstructions(Records.Prompt agentPrompt)
    {
        List<Records.GlobalPolicy> globalPolicies = morganaPrompt.GetAdditionalProperty<List<Records.GlobalPolicy>>("GlobalPolicies");
        StringBuilder sb = new StringBuilder();

        // Morgana framework layers
        sb.AppendLine(morganaPrompt.Target);
        sb.AppendLine();
        sb.AppendLine(morganaPrompt.Personality);
        sb.AppendLine();
        sb.AppendLine(FormatGlobalPolicies(globalPolicies));
        sb.AppendLine();
        sb.AppendLine(morganaPrompt.Instructions);
        sb.AppendLine();
        sb.AppendLine(morganaPrompt.Formatting);
        sb.AppendLine();

        // Domain layers
        sb.AppendLine(agentPrompt.Target);
        sb.AppendLine();
        sb.AppendLine(agentPrompt.Personality);
        sb.AppendLine();
        sb.AppendLine(agentPrompt.Instructions);
        sb.AppendLine();
        sb.AppendLine(agentPrompt.Formatting);
        sb.AppendLine();

        return sb.ToString();
    }

    /// <summary>
    /// Creates and configures a MorganaAIContextProvider for an agent with shared variable detection.
    /// Analyzes tool definitions to identify variables that participate in the conversation-scoped
    /// shared_context registry.
    /// </summary>
    /// <param name="agentName">Name of the agent for logging purposes (e.g., "billing")</param>
    /// <param name="tools">Tool definitions to scan for shared variable declarations</param>
    /// <param name="sharedContextCallback">
    /// Optional callback invoked when a shared variable is set. Wired to agent's
    /// OnSharedContextUpdate which persists the value via IConversationPersistenceService.
    /// </param>
    /// <returns>Configured MorganaAIContextProvider instance for the agent</returns>
    private MorganaAIContextProvider CreateAIContextProvider(
        string agentName,
        IEnumerable<Records.ToolDefinition> tools,
        Action<string, object>? sharedContextCallback = null)
    {
        // Derive the shared-variable allow-list from the tool definitions: a parameter is
        // cross-agent shared only if it is BOTH flagged Shared AND context-scoped. The
        // Scope=="context" guard is essential — a Shared but request-scoped parameter is
        // asked of the user every turn, not carried in the registry, so promoting it would
        // wrongly route a per-turn input into first-write-wins shared state. Flatten across
        // all tools and Distinct() because the same logical variable (e.g. "userId") is
        // typically declared on several tools and must register exactly once.
        List<string> sharedVariables = [.. tools
            .SelectMany(t => t.Parameters)
            .Where(p => p.Shared && string.Equals(p.Scope, "context", StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Name)
            .Distinct()];

        // Startup-visible diagnostic: the shared set is part of the cross-agent contract,
        // so surface it (or its emptiness) explicitly rather than leaving it implicit.
        logger.LogInformation(
            sharedVariables.Count > 0
                ? $"Agent '{agentName}' has {sharedVariables.Count} shared variables: {string.Join(", ", sharedVariables)}"
                : $"Agent '{agentName}' has NO shared variables");

        // The provider needs the allow-list up front: only writes to a name in this set
        // trigger OnSharedContextUpdate; everything else stays agent-local.
        MorganaAIContextProvider aiContextProvider = new MorganaAIContextProvider(logger, sharedVariables);

        // Wire persistence only when a callback was supplied. Left null (e.g. an agent
        // created outside the actor path) shared writes still update local state but are
        // not propagated to the conversation-scoped registry — no NPE, just no fan-out.
        if (sharedContextCallback != null)
            aiContextProvider.OnSharedContextUpdate = sharedContextCallback;

        return aiContextProvider;
    }

    /// <summary>
    /// Creates a <see cref="MorganaToolAdapter"/> with base tools always registered
    /// and optional intent-specific custom tools registered when a matching
    /// <see cref="MorganaTool"/> subclass is found in the tool registry.
    /// </summary>
    /// <param name="intent">Agent intent name.</param>
    /// <param name="tools">Merged tool definitions from morgana.json and agents.json.</param>
    /// <param name="toolContextFactory">Factory supplying the (provider, session) pair to tool constructors.</param>
    /// <returns>Configured MorganaToolAdapter with registered tool implementations</returns>
    private MorganaToolAdapter CreateToolAdapterForIntent(
        string intent,
        Records.ToolDefinition[] tools,
        Func<MorganaTool.ToolContext> toolContextFactory)
    {
        // The adapter needs the framework GlobalPolicies up front: it injects the
        // P0–P3 parameter guidance (ToolParameterContextGuidance / RequestGuidance) into
        // each generated AIFunction's parameter descriptions, so the LLM is told to check
        // context first vs. ask the user. Without them the tools still work but lose that
        // grounding nudge.
        List<Records.GlobalPolicy> globalPolicies = morganaPrompt.GetAdditionalProperty<List<Records.GlobalPolicy>>("GlobalPolicies");
        MorganaToolAdapter morganaToolAdapter = new MorganaToolAdapter(globalPolicies);

        // Split the merged set back into base (morgana.json) vs intent-specific
        // (agents.json). Compare by Name only: the incoming `tools` array was produced by
        // a Union that may carry distinct ToolDefinition instances for the same logical
        // tool, so reference/value equality would wrongly classify a base tool as
        // intent-specific. Name is the stable identity (tool method names are unique).
        Records.ToolDefinition[] baseTools = morganaPrompt.GetAdditionalProperty<Records.ToolDefinition[]>("Tools");
        Records.ToolDefinition[] intentSpecificTools = tools.Except(baseTools, new ToolDefinitionNameComparer()).ToArray();

        // ALWAYS register base tools (GetContextVariable, SetContextVariable,
        // SetQuickReplies, SetRichCard). They are implemented by the MorganaTool BASE
        // class itself — no subclass needed — so every agent gets them unconditionally,
        // even an MCP-only or tool-less one.
        MorganaTool baseTool = new MorganaTool(logger, toolContextFactory);
        RegisterToolsInAdapter(morganaToolAdapter, baseTool, baseTools);
        logger.LogInformation("Registered {BaseToolsLength} base tools for intent '{Intent}'", baseTools.Length, intent);

        // Base-tools-only agent: nothing domain-specific declared → done.
        if (intentSpecificTools.Length == 0)
        {
            logger.LogInformation("No intent-specific tools defined for intent '{Intent}' (agent has base tools only)", intent);
            return morganaToolAdapter;
        }

        // Domain tools are declared in agents.json but their methods live in a
        // [ProvidesToolForIntent] MorganaTool subclass discovered by reflection. Missing
        // implementation is a WARNING, not fatal: the agent stays usable on its base (and
        // any MCP) tools — degraded, not dead — and the ignored tools are named so the
        // mismatch is diagnosable.
        Type? toolType = toolRegistryService?.FindToolTypeForIntent(intent);
        if (toolType == null)
        {
            logger.LogWarning(
                $"Intent '{intent}' has {intentSpecificTools.Length} tool(s) defined in agents.json " +
                $"but no MorganaTool implementation found. Tools will be ignored: " +
                $"{string.Join(", ", intentSpecificTools.Select(t => t.Name))}");
            return morganaToolAdapter;
        }

        logger.LogInformation("Found custom native tool: {ToolTypeName} for intent '{Intent}' via ToolRegistry", toolType.Name, intent);

        // The implementation WAS found but cannot be constructed → this IS fatal (unlike
        // the missing-impl case above): a declared, discovered tool that can't instantiate
        // is a hard authoring bug, almost always a constructor that does not match the
        // required (ILogger, Func<MorganaTool.ToolContext>) signature. Fail loud with that
        // exact remediation rather than silently shipping an agent missing its domain tools.
        MorganaTool customToolInstance;
        try
        {
            customToolInstance = (MorganaTool)Activator.CreateInstance(toolType, logger, toolContextFactory)!;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to instantiate custom tool {ToolTypeName} for intent '{Intent}'", toolType.Name, intent);
            throw new InvalidOperationException(
                $"Could not create custom tool instance for intent '{intent}'. " +
                $"Ensure {toolType.Name} has a constructor accepting " +
                $"(ILogger, Func<MorganaTool.ToolContext>).", ex);
        }

        // Bind only the intent-specific definitions to the discovered instance (base tools
        // were already registered above against the base instance).
        RegisterToolsInAdapter(morganaToolAdapter, customToolInstance, intentSpecificTools);
        logger.LogInformation("Registered {Length} custom tools for intent '{Intent}'", intentSpecificTools.Length, intent);

        return morganaToolAdapter;
    }

    /// <summary>
    /// Registers tool methods from a MorganaTool instance into the MorganaToolAdapter.
    /// Uses reflection to create delegates for each tool method and validates against tool definitions.
    /// </summary>
    /// <param name="morganaToolAdapter">Target adapter to register tools into</param>
    /// <param name="toolInstance">
    /// MorganaTool instance containing the tool method implementations.
    /// Can be a base MorganaTool (for base tools) or a derived class like BillingTool (for custom tools).
    /// </param>
    /// <param name="tools">Tool definitions specifying which methods to register from the toolInstance</param>
    private void RegisterToolsInAdapter(
        MorganaToolAdapter morganaToolAdapter,
        MorganaTool toolInstance,
        Records.ToolDefinition[] tools)
    {
        foreach (Records.ToolDefinition toolDefinition in tools)
        {
            // Was `toolInstance.GetType().GetMethod(toolDefinition.Name)` — that single-argument
            // overload throws AmbiguousMatchException the instant the tool class declares two
            // methods sharing this name (e.g. an intentional GetOrders()/GetOrders(string)
            // overload pair), even though the tool definition below is perfectly unambiguous on
            // its own. ResolveToolMethod picks the right overload using the parameter names
            // agents.json actually declares.
            MethodInfo? method = ResolveToolMethod(toolInstance.GetType(), toolDefinition);
            if (method == null)
            {
                logger.LogWarning("Tool '{ToolDefinitionName}' declared in agents.json but not found in {Name}", toolDefinition.Name, toolInstance.GetType().Name);
                continue;
            }

            // Build a strongly-typed delegate whose exact Func<…> type is computed from
            // the method's own ParameterInfo at runtime: tool signatures are declared in
            // JSON configuration and bound to implementations via reflection, so the
            // concrete delegate type is unknowable at compile time.
            Delegate toolImplementation = Delegate.CreateDelegate(
                System.Linq.Expressions.Expression.GetDelegateType(
                    method.GetParameters().Select(p => p.ParameterType)
                                          .Concat([method.ReturnType])
                                          .ToArray()),
                toolInstance,
                method);

            morganaToolAdapter.AddTool(toolDefinition.Name, toolImplementation, toolDefinition);
        }
    }

    /// <summary>
    /// Resolves the <see cref="MethodInfo"/> backing a <see cref="Records.ToolDefinition"/> by name,
    /// tolerating C# method overloads that share that name.
    /// </summary>
    /// <remarks>
    /// <para><see cref="Type.GetMethod(string)"/> throws <see cref="AmbiguousMatchException"/> the
    /// moment two methods share a name, regardless of arity — a MorganaTool subclass declaring
    /// e.g. both <c>GetOrders()</c> and <c>GetOrders(string userId)</c> would crash agent creation
    /// entirely, for a tool definition that, on its own, looks perfectly valid. Overload resolution
    /// here reuses the only signal agents.json actually carries for a parameter — its NAME — since
    /// the JSON schema has no concept of a CLR type; this is the exact same signal
    /// <see cref="MorganaToolAdapter"/>'s ValidateToolDefinition already checks after resolution,
    /// just applied earlier to pick the right overload rather than an arbitrary one.</para>
    /// </remarks>
    /// <param name="toolType">Concrete MorganaTool subclass to search.</param>
    /// <param name="toolDefinition">Tool definition whose declared parameter names disambiguate overloads.</param>
    /// <returns>The resolved method, or <c>null</c> if no method with that name exists.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the declared parameter names match zero or more than one overload — a genuine
    /// agents.json/tool-class mismatch that must be fixed by the author, not silently guessed at.
    /// </exception>
    private static MethodInfo? ResolveToolMethod(Type toolType, Records.ToolDefinition toolDefinition)
    {
        // GetMethods() (plural) never throws on its own — it just enumerates. Filtering by name
        // ourselves, instead of handing that name straight to the throwing GetMethod(string)
        // overload, is what buys us the chance to disambiguate before .NET reflection gets a say.
        MethodInfo[] candidates = [.. toolType.GetMethods().Where(m => m.Name == toolDefinition.Name)];

        // The overwhelmingly common case — one method, one name, nothing to disambiguate — exits
        // here without ever building the parameter-name machinery below. FirstOrDefault() also
        // quietly covers "zero candidates" (unknown tool name), same as the old GetMethod did.
        if (candidates.Length <= 1)
            return candidates.FirstOrDefault();

        // Two or more C# methods share this name: only overload resolution by parameter NAME is
        // possible here, because that is the only signal agents.json carries for a parameter —
        // the JSON schema has no field for a CLR type, so "string userId" and "int userId" would
        // look identical to this method. This mirrors exactly what MorganaToolAdapter's
        // ValidateToolDefinition checks afterward; we are just checking it a step earlier, to
        // choose the right overload instead of whichever one .NET reflection happened to see first.
        HashSet<string> declaredParameterNames = [.. toolDefinition.Parameters.Select(p => p.Name)];

        MethodInfo[] matchingCandidates = [.. candidates.Where(m =>
        {
            ParameterInfo[] methodParams = m.GetParameters();
            return methodParams.Length == declaredParameterNames.Count
                   && methodParams.All(p => declaredParameterNames.Contains(p.Name!));
        })];

        // Exactly one survivor is the success path (this is what makes GetOrders()/GetOrders(string)
        // resolvable at all). Zero survivors means agents.json declared parameter names that don't
        // match ANY overload — an authoring mistake, not something to silently paper over. More than
        // one survivor means two overloads share both the name AND every parameter name (the
        // string-vs-int case from the docstring): genuinely undecidable from agents.json alone, so
        // this fails loud with an actionable message instead of guessing and binding the wrong one.
        return matchingCandidates.Length switch
        {
            1 => matchingCandidates[0],
            0 => throw new InvalidOperationException(
                $"Tool '{toolDefinition.Name}' has {candidates.Length} overload(s) on {toolType.Name}, but none " +
                $"matches the parameter names declared in agents.json ({string.Join(", ", declaredParameterNames)})."),
            _ => throw new InvalidOperationException(
                $"Tool '{toolDefinition.Name}' is ambiguous: {matchingCandidates.Length} overloads on {toolType.Name} " +
                $"share the exact same parameter names declared in agents.json ({string.Join(", ", declaredParameterNames)}). " +
                $"Overloads sharing both name and parameter names cannot be told apart — rename one of them.")
        };
    }

    /// <summary>
    /// Discovers tools from all MCP servers declared on the agent and registers them
    /// into the agent's tool adapter.
    /// </summary>
    /// <param name="agentType">Agent type to inspect for [UsesMCPServer] attributes</param>
    /// <param name="morganaToolAdapter">Target adapter to register discovered MCP tools into</param>
    /// <remarks>
    /// <para><strong>MCP Integration Flow:</strong></para>
    /// <list type="number">
    /// <item>Collect all [UsesMCPServer] attributes on the agent class</item>
    /// <item>If none found, skip (agent doesn't use MCP)</item>
    /// <item>For each attribute:
    ///   <list type="bullet">
    ///   <item>Get or create MCP client connection via IMCPClientRegistryService</item>
    ///   <item>Discover available tools via DiscoverToolsAsync</item>
    ///   <item>Convert MCP tools to Morgana format via MCPToolAdapter</item>
    ///   <item>Register converted tools in MorganaToolAdapter</item>
    ///   </list>
    /// </item>
    /// </list>
    /// </remarks>
    private void RegisterMCPTools(Type agentType, MorganaToolAdapter morganaToolAdapter)
    {
        // An agent may declare several [UsesMCPServer] (multiple servers, mixed
        // Http/Stdio) — collect them all, not just the first.
        UsesMCPServerAttribute[] attributes = agentType
            .GetCustomAttributes<UsesMCPServerAttribute>()
            .ToArray();

        // No MCP on this agent is the common, expected case (native-tool or tool-less
        // agents) — Debug, not Warning: it is not a problem, just not applicable.
        if (attributes.Length == 0)
        {
            logger.LogDebug("Agent {AgentTypeName} does not use MCP servers", agentType.Name);
            return;
        }

        logger.LogInformation("Agent {AgentTypeName} declares {AttributesLength} MCP server(s)", agentType.Name, attributes.Length);

        foreach (UsesMCPServerAttribute attribute in attributes)
        {
            // Per-server isolation is the whole point of this loop: each server is
            // attempted independently and a failure (unreachable host, bad URI, discovery
            // error) is logged and swallowed so it cannot abort the remaining servers or
            // agent creation. This is what makes MCP registration "best-effort" — a dead
            // server costs that server's tools, nothing more. RegisterMCPToolsFromServer
            // itself already absorbs a terminated session via the reconnect-safe path; a
            // throw reaching here means a hard, non-transient fault for THAT server only.
            try
            {
                RegisterMCPToolsFromServer(attribute, morganaToolAdapter);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to register MCP tools from server: {AttributeCommand}", attribute.Command);
            }
        }
    }

    /// <summary>
    /// Registers tools from a single MCP server into the MorganaToolAdapter.
    /// Handles connection, tool discovery, conversion, and registration.
    /// </summary>
    /// <param name="serverAttribute">Attribute declaring the MCP server (transport, command, args)</param>
    /// <param name="morganaToolAdapter">Target adapter to register discovered tools into</param>
    /// <remarks>
    /// <para><strong>Tool naming:</strong></para>
    /// <para>
    /// Tools are registered under the names the MCP server itself declares (e.g.
    /// <c>get_weather</c>). No Morgana-side prefix or namespacing is added, so the LLM
    /// calls them by their native server name exactly as the server advertises them.
    /// </para>
    /// </remarks>
    private void RegisterMCPToolsFromServer(UsesMCPServerAttribute serverAttribute, MorganaToolAdapter morganaToolAdapter)
    {
        logger.LogInformation("Registering MCP tools from server: {ServerAttributeCommand}", serverAttribute.Command);

        // Discover through the reconnecting wrapper: an MCP host whose session store does
        // not survive instance recycling or scale-out can drop the cached client's session
        // between connect and tool listing (the spec-mandated HTTP 404 on a session-bearing
        // request). The wrapper transparently re-initializes and retries once instead of
        // failing registration outright.
        IList<ModelContextProtocol.Protocol.Tool> mcpTools = imcpClientRegistryService
            .ExecuteWithReconnectAsync(serverAttribute, client => client.DiscoverToolsAsync())
            .GetAwaiter()
            .GetResult();

        // A reachable server that advertises zero tools is not an error (it may expose
        // none yet, or only prompts/resources): warn for visibility and return — there is
        // simply nothing to bind, and the agent keeps its base/native tools.
        if (mcpTools.Count == 0)
        {
            logger.LogWarning("No tools discovered from MCP server: {ServerAttributeCommand}", serverAttribute.Command);
            return;
        }

        logger.LogInformation("Discovered {McpToolsCount} tools from MCP server: {ServerAttributeCommand}", mcpTools.Count, serverAttribute.Command);

        // Take the live pooled client AFTER discovery: if the wrapper had to reconnect,
        // the original client was disposed and the pool now holds the fresh one — the
        // MCPToolAdapter must bind to that, not a stale reference to the dead session.
        MCPClient mcpClient = imcpClientRegistryService.GetOrCreateClientAsync(serverAttribute)
            .GetAwaiter()
            .GetResult();

        // Bridge each MCP tool into a native Morgana tool: ConvertTools IL-generates a
        // typed delegate (correct parameter names/types) per remote tool and pairs it with
        // a ToolDefinition, keyed by the server-declared tool name.
        MCPToolAdapter mcpToolAdapter = new MCPToolAdapter(mcpClient, logger);
        Dictionary<string, (Delegate toolDelegate, Records.ToolDefinition toolDefinition)> convertedTools =
            mcpToolAdapter.ConvertTools(mcpTools.ToList());

        foreach (KeyValuePair<string, (Delegate toolDelegate, Records.ToolDefinition toolDefinition)> kvp in convertedTools)
        {
            // Per-tool isolation, mirroring the per-server loop: one malformed/unbindable
            // tool is logged and skipped so the rest of this server's tools still register.
            try
            {
                // Force the definition's Name to the dictionary key (the canonical
                // server-declared name) so registration and the LLM-visible name agree
                // exactly — no prefix, no drift from whatever the source definition carried.
                Records.ToolDefinition namedToolDefinition = kvp.Value.toolDefinition with { Name = kvp.Key };
                morganaToolAdapter.AddTool(kvp.Key, kvp.Value.toolDelegate, namedToolDefinition);
                logger.LogInformation("Registered MCP tool: {KvpKey}", kvp.Key);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to register MCP tool: {KvpKey} from {ServerAttributeCommand}", kvp.Key, serverAttribute.Command);
            }
        }

        logger.LogInformation("Successfully registered {ConvertedToolsCount} MCP tools from {ServerAttributeCommand}", convertedTools.Count, serverAttribute.Command);
    }

    private class ToolDefinitionNameComparer : IEqualityComparer<Records.ToolDefinition>
    {
        public bool Equals(Records.ToolDefinition? x, Records.ToolDefinition? y)
        {
            if (ReferenceEquals(x, y))
                return true;
            if (x is null || y is null)
                return false;
            return string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);
        }

        public int GetHashCode(Records.ToolDefinition obj) =>
            obj.Name.GetHashCode(StringComparison.OrdinalIgnoreCase);
    }
}