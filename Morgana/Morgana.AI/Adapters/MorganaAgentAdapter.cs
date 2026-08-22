using System.Reflection;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using Morgana.AI.Abstractions;
using Morgana.AI.Attributes;
using Morgana.AI.ChatClients;
using Morgana.AI.Interfaces;
using Morgana.AI.Providers;
using Morgana.AI.Services;

namespace Morgana.AI.Adapters;

// This suppresses the experimental API warning for IChatReducer usage.
// Microsoft marks IChatReducer as experimental (MEAI001) but recommends it
// for production use in context window management scenarios.
#pragma warning disable MEAI001 // Type is for evaluation purposes only and is subject to change or removal in future updates

/// <summary>
/// Creates and configures AIAgent instances from Morgana agent definitions.
/// Handles instruction composition, tool/MCP registration, provider setup.
/// Uses session accessor pattern: MorganaAIContextProvider + Func&lt;ToolContext&gt; factory.
/// </summary>
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
    protected readonly HistoryReducerService chatReducerService;

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
    /// Assembles everything the agent's model reads: the composed system prompt and the tool
    /// descriptions. Passed on to <see cref="MorganaToolAdapter"/> and
    /// <see cref="MorganaAIContextProvider"/>, which compose their own fragments through it.
    /// </summary>
    protected readonly IPromptComposerService promptComposerService;

    /// <summary>
    /// Initializes a new instance of the MorganaAgentAdapter.
    /// Loads the Morgana framework prompt for later composition with domain prompts.
    /// </summary>
    /// <param name="llmService">LLM service abstraction, queried per-agent for its declared tier's chat client and pricing</param>
    /// <param name="promptResolverService">Service for resolving prompt templates</param>
    /// <param name="promptComposerService">Service composing prompts and tool descriptions for the model</param>
    /// <param name="toolRegistryService">Service for discovering custom MorganaTool implementations</param>
    /// <param name="imcpClientRegistryService">Service for managing MCP server connections</param>
    /// <param name="chatReducerService">Service for reducing context window sent to LLM</param>
    /// <param name="dustLimitService">Per-conversation lifetime token-budget limiter</param>
    /// <param name="logger">Logger instance for diagnostics</param>
    public MorganaAgentAdapter(
        ILLMService llmService,
        IPromptResolverService promptResolverService,
        IPromptComposerService promptComposerService,
        IToolRegistryService toolRegistryService,
        IMCPClientRegistryService imcpClientRegistryService,
        HistoryReducerService chatReducerService,
        IDustLimitService dustLimitService,
        ILogger logger)
    {
        this.llmService = llmService;
        this.promptResolverService = promptResolverService;
        this.promptComposerService = promptComposerService;
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
        // The single sync-over-async point of the whole creation path, and it is a structural
        // boundary rather than a shortcut: a MorganaAgent is materialized by Akka through
        // DependencyResolver.Props, i.e. inside a constructor, which offers no async seam. Everything
        // below this line is properly awaited; callers that DO have one — Forge composing a draft
        // agent, or a future async actor-initialization pattern — should call CreateAgentAsync
        // directly and never come through here.
        => CreateAgentAsync(agentType, conversationId, sessionAccessor, sharedContextCallback)
            .GetAwaiter()
            .GetResult();

    /// <summary>
    /// Asynchronous counterpart of <see cref="CreateAgent"/>, and the real implementation: prompt
    /// resolution, prompt composition and tool-description assembly are all awaited here.
    /// </summary>
    /// <inheritdoc cref="CreateAgent" path="/param"/>
    /// <returns>
    /// A tuple of (AIAgent, MorganaAIContextProvider, MorganaChatHistoryProvider) —
    /// all three singletons for this agent instance.
    /// </returns>
    public async Task<(AIAgent agent, MorganaAIContextProvider contextProvider, MorganaChatHistoryProvider historyProvider)> CreateAgentAsync(
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
        //    resolved from agents.json.
        Records.Prompt agentPrompt = await promptResolverService.ResolveAsync(intentAttribute.Intent);

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

        // 6b) Collect the tools of every [UsesMCPServer] on the agent. Best-effort by
        //     design — a server that is down or misconfigured is logged per-server and skipped, never aborting
        //     agent creation (an MCP-only agent simply ends up with no tools rather than
        //     failing to exist). They stay apart from the native adapter because they need
        //     nothing from it: each one arrives already an AIFunction.
        List<AIFunction> mcpTools = RegisterMCPTools(agentType);

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
                    Instructions = await promptComposerService.ComposeAgentInstructionsAsync(agentPrompt),
                    Tools = [.. await morganaToolAdapter.CreateAllFunctionsAsync(), .. mcpTools]
                }
            });

        // 10) Return all three: the caller (MorganaAgent subclass) keeps the provider and
        //     history-provider handles to drive context/history across turns — the agent
        //     alone is not enough because providers are queried/mutated outside InvokeAsync.
        return (aiAgent, morganaAIContextProvider, chatHistoryProvider);
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
        // all tools and Distinct() because the same logical variable (e.g. "customerCode") is
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
        // trigger OnSharedContextUpdate; everything else stays agent-local. The composer goes
        // with it because the held-context declaration is assembled per turn, not now: it names
        // the variables the session holds at that moment, which nobody knows at creation time.
        MorganaAIContextProvider aiContextProvider =
            new MorganaAIContextProvider(logger, sharedVariables, promptComposerService: promptComposerService);

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
        // The adapter composes the description of each generated AIFunction through the composer,
        // which splices ToolDescriptionContextGuidance into the tools declaring context-scoped
        // parameters. Parameter descriptions carry no framework template at all — see
        // MorganaToolAdapter.CreateFunctionAsync.
        MorganaToolAdapter morganaToolAdapter = new MorganaToolAdapter(promptComposerService);

        // Split the merged set back into base (morgana.json) vs intent-specific
        // (agents.json). Compare by Name only: the incoming `tools` array was produced by
        // a Union that may carry distinct ToolDefinition instances for the same logical
        // tool, so reference/value equality would wrongly classify a base tool as
        // intent-specific. Name is the stable identity (tool method names are unique).
        Records.ToolDefinition[] baseTools = morganaPrompt.GetAdditionalProperty<Records.ToolDefinition[]>("Tools");
        Records.ToolDefinition[] intentSpecificTools = [.. tools.Except(baseTools, new ToolDefinitionNameComparer())];

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
            MethodInfo? method = toolInstance.GetType().GetMethod(toolDefinition.Name);
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
    /// Discovers tools from every MCP server declared on the agent.
    /// Collects [UsesMCPServer] attributes and discovers tools from each.
    /// McpClientTool instances are already AIFunctions; no schema conversion applied.
    /// </summary>
    /// <param name="agentType">Agent type to inspect for [UsesMCPServer] attributes</param>
    /// <returns>Discovered tools as AIFunctions, empty if no servers declared</returns>
    private List<AIFunction> RegisterMCPTools(Type agentType)
    {
        // An agent may declare several [UsesMCPServer] (multiple servers, mixed
        // Http/Stdio) — collect them all, not just the first.
        UsesMCPServerAttribute[] attributes = [.. agentType.GetCustomAttributes<UsesMCPServerAttribute>()];

        // No MCP on this agent is the common, expected case (native-tool or tool-less
        // agents) — Debug, not Warning: it is not a problem, just not applicable.
        if (attributes.Length == 0)
        {
            logger.LogDebug("Agent {AgentTypeName} does not use MCP servers", agentType.Name);
            return [];
        }

        logger.LogInformation("Agent {AgentTypeName} declares {AttributesLength} MCP server(s)", agentType.Name, attributes.Length);

        List<AIFunction> mcpTools = [];

        foreach (UsesMCPServerAttribute attribute in attributes)
        {
            // Per-server isolation is the whole point of this loop: each server is
            // attempted independently and a failure (unreachable host, bad URI, discovery
            // error) is logged and swallowed so it cannot abort the remaining servers or
            // agent creation. This is what makes MCP registration "best-effort" — a dead
            // server costs that server's tools, nothing more.
            try
            {
                mcpTools.AddRange(DiscoverMCPToolsFromServer(attribute));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to register MCP tools from server: {AttributeCommand}", attribute.Command);
            }
        }

        return mcpTools;
    }

    /// <summary>
    /// Discovers tools from a single MCP server. Tools retain server-declared names and schema
    /// untouched — <see cref="McpClientTool"/> already is an <see cref="AIFunction"/>, so no
    /// wrapping or conversion is applied.
    /// </summary>
    /// <param name="serverAttribute">Attribute declaring the MCP server (transport, command, args)</param>
    /// <returns>Server's tools as AIFunctions</returns>
    private IList<AIFunction> DiscoverMCPToolsFromServer(UsesMCPServerAttribute serverAttribute)
    {
        logger.LogInformation("Registering MCP tools from server: {ServerAttributeCommand}", serverAttribute.Command);

        MCPClient mcpClient = imcpClientRegistryService.GetOrCreateClientAsync(serverAttribute).GetAwaiter().GetResult();
        IList<McpClientTool> mcpTools = mcpClient.DiscoverToolsAsync().GetAwaiter().GetResult();

        // A reachable server that advertises zero tools is not an error (it may expose
        // none yet, or only prompts/resources): warn for visibility and return — there is
        // simply nothing to bind, and the agent keeps its base/native tools.
        if (mcpTools.Count == 0)
        {
            logger.LogWarning("No tools discovered from MCP server: {ServerAttributeCommand}", serverAttribute.Command);
            return [];
        }

        foreach (McpClientTool mcpTool in mcpTools)
            logger.LogInformation("Registered MCP tool: {McpToolName}", mcpTool.Name);

        logger.LogInformation("Successfully registered {McpToolsCount} MCP tools from {ServerAttributeCommand}", mcpTools.Count, serverAttribute.Command);

        return [.. mcpTools];
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