using System.Reflection;
using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Morgana.Agents.Abstractions;
using Morgana.Agents.Attributes;
using Morgana.Agents.Interfaces;
using Morgana.Agents.Providers;
using Morgana.Agents.Services;
using Morgana.Foundations;
using Morgana.Foundations.Interfaces;

namespace Morgana.Agents.Adapters;

/// <summary>
/// Adapter for creating and configuring AIAgent instances from Morgana agent definitions.
/// Handles agent instantiation, instruction composition, tool registration, context provider setup and MCP integration.
/// </summary>
/// <remarks>
/// <para><strong>Purpose:</strong></para>
/// <para>MorganaAgentAdapter is the central factory for creating fully-configured AI agents with tool calling capabilities.
/// It orchestrates the composition of agent instructions, registration of native and MCP tools, context provider setup,
/// and integration with the Microsoft.Agents.AI framework.</para>
/// <para><strong>Key Responsibilities:</strong></para>
/// <list type="bullet">
/// <item><term>Instruction Composition</term><description>Merges framework prompts (morgana.json) with domain prompts (agents.json)</description></item>
/// <item><term>Base Tools Registration</term><description>Ensures every agent has GetContextVariable, SetContextVariable, SetQuickReplies</description></item>
/// <item><term>Custom Tools Registration</term><description>Discovers and registers intent-specific MorganaTool implementations</description></item>
/// <item><term>MCP Integration</term><description>Discovers and registers tools from external MCP servers</description></item>
/// <item><term>Context Provider Setup</term><description>Configures shared variable broadcasting and context isolation</description></item>
/// </list>
/// <para><strong>Agent Creation Flow:</strong></para>
/// <code>
/// 1. Extract intent from [HandlesIntent] attribute
/// 2. Load domain prompt from agents.json
/// 3. Compose full instructions (framework + domain)
/// 4. Create context provider with shared variable configuration
/// 5. Register base tools (ALWAYS - GetContextVariable, SetContextVariable, SetQuickReplies)
/// 6. Register custom tools (IF MorganaTool exists for intent)
/// 7. Register MCP tools (IF [UsesMCPServers] attribute present)
/// 8. Create AIAgent with chatClient.CreateAIAgent()
/// 9. Return (agent, contextProvider) tuple
/// </code>
/// <para><strong>Tool Registration Strategy (NEW in this version):</strong></para>
/// <para>The adapter now guarantees that ALL agents receive the 3 fundamental tools from MorganaTool,
/// regardless of whether a custom MorganaTool implementation exists for the intent. This enables
/// "MCP-only" agents that rely entirely on external tools while still having context access and
/// quick reply capabilities.</para>
/// <code>
/// // Traditional agent with custom tools
/// [HandlesIntent("billing")]
/// [ProvidesToolForIntent("billing")] // BillingTool exists
/// public class BillingAgent : MorganaAgent
/// Result: 3 base tools + 2 BillingTool methods + 0 MCP tools = 5 tools
///
/// // Modern MCP-only agent (NEW SCENARIO SUPPORTED)
/// [HandlesIntent("research")]
/// [UsesMCPServers("brave-search")]
/// // No [ProvidesToolForIntent] needed!
/// public class ResearchAgent : MorganaAgent
/// Result: 3 base tools + 0 custom tools + 15 MCP tools = 18 tools
/// </code>
/// <para><strong>Usage Pattern:</strong></para>
/// <code>
/// // In MorganaAgent constructor
/// public BillingAgent(
///     string conversationId,
///     ILLMService llmService,
///     IPromptResolverService promptResolverService,
///     ILogger agentLogger,
///     MorganaAgentAdapter agentAdapter)
///     : base(conversationId, llmService, promptResolverService, agentLogger)
/// {
///     (aiAgent, contextProvider) = agentAdapter.CreateAgent(
///         GetType(),
///         OnSharedContextUpdate);
///
///     ReceiveAsync&lt;Records.AgentRequest&gt;(ExecuteAgentAsync);
/// }
/// </code>
/// </remarks>
public class MorganaAgentAdapter
{
    /// <summary>
    /// Service for resolving prompt templates from configuration sources (morgana.json, agents.json).
    /// </summary>
    protected readonly IPromptResolverService promptResolverService;

    /// <summary>
    /// Microsoft.Extensions.AI chat client for creating AIAgent instances with tool calling support.
    /// Wraps the underlying LLM provider (Anthropic, Azure OpenAI, etc.).
    /// </summary>
    protected readonly IChatClient chatClient;

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
    /// <param name="chatClient">Microsoft.Extensions.AI chat client for AIAgent creation</param>
    /// <param name="promptResolverService">Service for resolving prompt templates</param>
    /// <param name="toolRegistryService">Service for discovering custom MorganaTool implementations</param>
    /// <param name="imcpClientRegistryService">Service for managing MCP server connections</param>
    /// <param name="logger">Logger instance for diagnostics</param>
    /// <remarks>
    /// <para><strong>Initialization:</strong></para>
    /// <para>The adapter loads the Morgana framework prompt synchronously during construction to ensure
    /// it's available for all subsequent CreateAgent calls. This is acceptable because prompt loading
    /// is fast (embedded resource or cached configuration).</para>
    /// <para><strong>Dependency Injection:</strong></para>
    /// <code>
    /// // Program.cs registration
    /// builder.Services.AddSingleton&lt;MorganaAgentAdapter&gt;();
    ///
    /// // All dependencies are already registered:
    /// // - IChatClient via ILLMService.GetChatClient()
    /// // - IPromptResolverService
    /// // - IToolRegistryService
    /// // - IMCPClientRegistryService
    /// </code>
    /// </remarks>
    public MorganaAgentAdapter(
        IChatClient chatClient,
        IPromptResolverService promptResolverService,
        IToolRegistryService toolRegistryService,
        IMCPClientRegistryService imcpClientRegistryService,
        ILogger logger)
    {
        this.chatClient = chatClient;
        this.promptResolverService = promptResolverService;
        this.toolRegistryService = toolRegistryService;
        this.imcpClientRegistryService = imcpClientRegistryService;
        this.logger = logger;

        morganaPrompt = promptResolverService.ResolveAsync("Morgana").GetAwaiter().GetResult();
    }

    /// <summary>
    /// Formats global policies from Morgana configuration into a multi-line string for agent instructions.
    /// Policies are ordered by Type (Critical first, then Operational) and Priority (lower number = higher priority).
    /// </summary>
    /// <param name="policies">List of global policies from morgana.json</param>
    /// <returns>Formatted string with one policy per line: "PolicyName: PolicyDescription"</returns>
    /// <remarks>
    /// <para><strong>Policy Types:</strong></para>
    /// <list type="bullet">
    /// <item><term>Critical</term><description>Non-negotiable rules (e.g., context handling, never mention technical details)</description></item>
    /// <item><term>Operational</term><description>Behavioral guidelines (e.g., #INT# token usage, parameter guidance)</description></item>
    /// </list>
    /// <para><strong>Output Format:</strong></para>
    /// <code>
    /// ContextHandling: CRITICAL RULE ABOUT CONTEXT - Before asking for ANY information...
    /// InteractiveToken: OPERATIONAL RULE ABOUT INTERACTION TOKEN '#INT#' - Ensure to respect...
    /// ToolParameterContextGuidance: BEFORE INVOKING THIS TOOL: call the GetContextVariable tool...
    /// </code>
    /// <para><strong>Usage:</strong></para>
    /// <para>This formatted string is injected into agent instructions during ComposeAgentInstructions,
    /// ensuring all agents follow the same critical rules regardless of their domain.</para>
    /// </remarks>
    private string FormatGlobalPolicies(List<Records.GlobalPolicy> policies)
    {
        StringBuilder sb = new StringBuilder();

        foreach (Records.GlobalPolicy policy in policies.OrderBy(p => p.Type)
                                                .ThenBy(p => p.Priority))
        {
            sb.AppendLine($"{policy.Name}: {policy.Description}");
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Composes full agent instructions by merging framework prompts (morgana.json) with domain prompts (agents.json).
    /// Creates a comprehensive instruction set that defines agent behavior, personality, and operational rules.
    /// </summary>
    /// <param name="agentPrompt">Domain-specific prompt for the agent (e.g., "billing", "contract")</param>
    /// <returns>Complete instruction string for AIAgent creation</returns>
    /// <remarks>
    /// <para><strong>Composition Order:</strong></para>
    /// <list type="number">
    /// <item>Morgana Content (framework role definition)</item>
    /// <item>Morgana Personality (witch persona, magical tone)</item>
    /// <item>Global Policies (formatted critical and operational rules)</item>
    /// <item>Morgana Instructions (base conversation guidelines)</item>
    /// <item>Morgana Formatting (text formatting preferences)</item>
    /// <item>Agent Content (domain-specific role, e.g., "You know the book of spells called 'Billing'")</item>
    /// <item>Agent Personality (domain-specific tone, e.g., "formal and pragmatic witch")</item>
    /// <item>Agent Instructions (domain-specific behavioral rules)</item>
    /// <item>Agent Formatting (domain-specific formatting overrides, if any)</item>
    /// </list>
    /// <para><strong>Example Result (BillingAgent):</strong></para>
    /// <code>
    /// TARGET: You are a digital assistant. You listen to user requests...
    ///
    /// PERSONALITY: Your name is Morgana: you are a 'good witch'...
    ///
    /// ContextHandling: CRITICAL RULE ABOUT CONTEXT - Before asking for ANY information...
    /// InteractiveToken: OPERATIONAL RULE ABOUT INTERACTION TOKEN '#INT#'...
    ///
    /// INSTRUCTIONS: Ongoing conversation between you (assistant) and user...
    ///
    /// FORMATTING: Prefer clean, readable text formatting...
    ///
    /// You know the book of spells called 'Billing and Payments'...
    ///
    /// You are a formal and pragmatic witch, focused on precision...
    ///
    /// Never invent procedures you don't explicitly possess...
    ///
    /// [Domain-specific formatting if any]
    /// </code>
    /// <para><strong>Design Rationale:</strong></para>
    /// <para>Framework prompts establish the base personality and critical rules that apply to ALL agents,
    /// while domain prompts overlay specialized knowledge and behavioral nuances for specific intents.
    /// This layered approach ensures consistency across agents while enabling domain customization.</para>
    /// </remarks>
    private string ComposeAgentInstructions(Records.Prompt agentPrompt)
    {
        List<Records.GlobalPolicy> globalPolicies = morganaPrompt.GetAdditionalProperty<List<Records.GlobalPolicy>>("GlobalPolicies");
        StringBuilder sb = new StringBuilder();

        sb.AppendLine(morganaPrompt.Content);
        sb.AppendLine();
        sb.AppendLine(morganaPrompt.Personality);
        sb.AppendLine();
        sb.AppendLine(FormatGlobalPolicies(globalPolicies));
        sb.AppendLine();
        sb.AppendLine(morganaPrompt.Instructions);
        sb.AppendLine();
        sb.AppendLine(morganaPrompt.Formatting);
        sb.AppendLine();

        sb.AppendLine(agentPrompt.Content);
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
    /// Creates a fully configured AIAgent instance from a Morgana agent type.
    /// Orchestrates instruction composition, tool registration, context provider setup, and MCP integration.
    /// </summary>
    /// <param name="agentType">
    /// Type of the agent class decorated with [HandlesIntent] attribute.
    /// Must be a subclass of MorganaAgent.
    /// </param>
    /// <param name="sharedContextCallback">
    /// Optional callback invoked when agent sets a shared context variable.
    /// Typically wired to MorganaAgent.OnSharedContextUpdate for broadcasting to RouterActor.
    /// </param>
    /// <returns>
    /// Tuple containing:
    /// - agent: Configured AIAgent instance ready for LLM interactions
    /// - provider: MorganaContextProvider instance for this agent's context management
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown if agentType is not decorated with [HandlesIntent] attribute
    /// </exception>
    /// <remarks>
    /// <para><strong>Creation Flow:</strong></para>
    /// <list type="number">
    /// <item>Validate [HandlesIntent] attribute presence</item>
    /// <item>Extract intent name from attribute</item>
    /// <item>Load domain prompt from agents.json via intent name</item>
    /// <item>Merge tool definitions (morgana.json + agents.json)</item>
    /// <item>Create context provider with shared variable configuration</item>
    /// <item>Create tool adapter and register tools:
    ///   <list type="bullet">
    ///   <item>Base tools (ALWAYS): GetContextVariable, SetContextVariable, SetQuickReplies</item>
    ///   <item>Custom tools (IF ProvidesToolForIntent exists): Domain-specific MorganaTool methods</item>
    ///   <item>MCP tools (IF UsesMCPServers attribute): External server tools</item>
    ///   </list>
    /// </item>
    /// <item>Compose full agent instructions (framework + domain)</item>
    /// <item>Create AIAgent with chatClient.CreateAIAgent()</item>
    /// <item>Return (agent, contextProvider) tuple</item>
    /// </list>
    /// <para><strong>Tool Registration Guarantee (NEW):</strong></para>
    /// <para>This version ALWAYS registers the 3 base tools from MorganaTool, even if no custom
    /// MorganaTool implementation exists for the intent. This enables "MCP-only" agents that rely
    /// entirely on external tools while still having context access and quick reply capabilities.</para>
    /// <code>
    /// // Traditional agent with custom tools
    /// [HandlesIntent("billing")]
    /// public class BillingAgent : MorganaAgent
    /// // BillingTool exists with [ProvidesToolForIntent("billing")]
    /// Result: 3 base + 2 custom + 0 MCP = 5 tools
    ///
    /// // Modern MCP-only agent (NEW SCENARIO)
    /// [HandlesIntent("research")]
    /// [UsesMCPServers("brave-search")]
    /// public class ResearchAgent : MorganaAgent
    /// // NO BillingTool needed!
    /// Result: 3 base + 0 custom + 15 MCP = 18 tools
    /// </code>
    /// <para><strong>Usage Example:</strong></para>
    /// <code>
    /// public class BillingAgent : MorganaAgent
    /// {
    ///     public BillingAgent(
    ///         string conversationId,
    ///         ILLMService llmService,
    ///         IPromptResolverService promptResolverService,
    ///         ILogger agentLogger,
    ///         MorganaAgentAdapter agentAdapter)
    ///         : base(conversationId, llmService, promptResolverService, agentLogger)
    ///     {
    ///         // Create agent with tool calling capabilities
    ///         (aiAgent, contextProvider) = agentAdapter.CreateAgent(
    ///             GetType(),
    ///             OnSharedContextUpdate);
    ///
    ///         // Register message handler
    ///         ReceiveAsync&lt;Records.AgentRequest&gt;(ExecuteAgentAsync);
    ///     }
    /// }
    /// </code>
    /// <para><strong>Context Provider Callback:</strong></para>
    /// <para>The sharedContextCallback enables cross-agent coordination. When an agent sets a shared
    /// variable (e.g., userId), the callback triggers broadcasting to other agents via RouterActor:</para>
    /// <code>
    /// // In MorganaAgent
    /// protected void OnSharedContextUpdate(string key, object value)
    /// {
    ///     Context.ActorSelection($"/user/router-{conversationId}")
    ///         .Tell(new Records.BroadcastContextUpdate(intent, new Dictionary&lt;string, object&gt; { [key] = value }));
    /// }
    /// </code>
    /// </remarks>
    public (AIAgent agent, MorganaContextProvider provider) CreateAgent(
        Type agentType,
        Action<string, object>? sharedContextCallback = null)
    {
        HandlesIntentAttribute? intentAttribute = agentType.GetCustomAttribute<HandlesIntentAttribute>();
        if (intentAttribute == null)
            throw new InvalidOperationException($"Agent type '{agentType.Name}' must be decorated with [HandlesIntent] attribute");

        string intent = intentAttribute.Intent;

        logger.LogInformation($"Creating agent for intent '{intent}'...");

        Records.Prompt agentPrompt = promptResolverService.ResolveAsync(intent).GetAwaiter().GetResult();

        Records.ToolDefinition[] agentTools = [.. morganaPrompt.GetAdditionalProperty<Records.ToolDefinition[]>("Tools")
                                            .Union(agentPrompt.GetAdditionalProperty<Records.ToolDefinition[]>("Tools"))];

        MorganaContextProvider contextProvider = CreateContextProvider(intent, agentTools, sharedContextCallback);

        MorganaToolAdapter morganaToolAdapter = CreateToolAdapterForIntent(intent, agentTools, contextProvider);

        RegisterMCPTools(agentType, morganaToolAdapter);

        string instructions = ComposeAgentInstructions(agentPrompt);

        AIAgent agent = chatClient.CreateAIAgent(
            instructions: instructions,
            name: intent,
            tools: [.. morganaToolAdapter.CreateAllFunctions()]);

        return (agent, contextProvider);
    }

    /// <summary>
    /// Creates and configures a MorganaContextProvider for an agent with shared variable detection.
    /// Analyzes tool definitions to identify variables that should be broadcast across agents.
    /// </summary>
    /// <param name="agentName">Name of the agent for logging purposes (e.g., "billing")</param>
    /// <param name="tools">Tool definitions to scan for shared variable declarations</param>
    /// <param name="sharedContextCallback">
    /// Optional callback invoked when a shared variable is set.
    /// Wired to agent's OnSharedContextUpdate for broadcasting to RouterActor.
    /// </param>
    /// <returns>Configured MorganaContextProvider instance for the agent</returns>
    /// <remarks>
    /// <para><strong>Shared Variable Detection:</strong></para>
    /// <para>Scans all tool parameters for those marked with Scope="context" and Shared=true.
    /// These variables will trigger the callback when set via SetContextVariable tool.</para>
    /// <code>
    /// // In agents.json tool definition
    /// {
    ///   "Name": "userId",
    ///   "Description": "Alphanumeric identifier of the user",
    ///   "Required": true,
    ///   "Scope": "context",
    ///   "Shared": true  // ← Triggers cross-agent broadcasting
    /// }
    ///
    /// // Result
    /// Agent 'billing' has 1 shared variables: userId
    /// </code>
    /// <para><strong>Context Isolation:</strong></para>
    /// <para>Each agent gets its own MorganaContextProvider instance, ensuring context isolation
    /// between agents. Shared variables are synchronized via explicit broadcasting through RouterActor,
    /// not via shared state.</para>
    /// <para><strong>Callback Wiring:</strong></para>
    /// <code>
    /// MorganaContextProvider provider = new MorganaContextProvider(logger, sharedVariables);
    /// provider.OnSharedContextUpdate = sharedContextCallback;
    ///
    /// // Later, when tool sets shared variable:
    /// provider.SetVariable("userId", "P994E");
    /// // → Detects "userId" is shared
    /// // → Invokes callback("userId", "P994E")
    /// // → Agent broadcasts to RouterActor
    /// // → RouterActor sends to all other agents
    /// </code>
    /// <para><strong>Logging Output:</strong></para>
    /// <code>
    /// // Agent with shared variables
    /// Agent 'billing' has 2 shared variables: userId, customerId
    ///
    /// // Agent without shared variables
    /// Agent 'troubleshooting' has NO shared variables
    /// </code>
    /// </remarks>
    private MorganaContextProvider CreateContextProvider(
        string agentName,
        IEnumerable<Records.ToolDefinition> tools,
        Action<string, object>? sharedContextCallback = null)
    {
        List<string> sharedVariables = tools
            .SelectMany(t => t.Parameters)
            .Where(p => p.Shared && string.Equals(p.Scope, "context", StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Name)
            .Distinct()
            .ToList();

        logger.LogInformation(
            sharedVariables.Count > 0
                ? $"Agent '{agentName}' has {sharedVariables.Count} shared variables: {string.Join(", ", sharedVariables)}"
                : $"Agent '{agentName}' has NO shared variables");

        MorganaContextProvider provider = new MorganaContextProvider(logger, sharedVariables);

        if (sharedContextCallback != null)
            provider.OnSharedContextUpdate = sharedContextCallback;

        return provider;
    }

    /// <summary>
    /// Creates a MorganaToolAdapter with guaranteed base tool registration and optional custom tool registration.
    /// This is the core method implementing the "base tools always + custom tools if available" strategy.
    /// </summary>
    /// <param name="intent">Intent name for tool discovery (e.g., "billing", "contract")</param>
    /// <param name="tools">All tool definitions (morgana.json + agents.json already merged)</param>
    /// <param name="contextProvider">Context provider instance for tool access to conversation variables</param>
    /// <returns>Configured MorganaToolAdapter with registered tool implementations</returns>
    /// <remarks>
    /// <para><strong>NEW BEHAVIOR - Base Tools Always Registered:</strong></para>
    /// <para>This version guarantees that ALL agents receive the 3 fundamental tools from MorganaTool,
    /// regardless of whether a custom MorganaTool implementation exists for the intent. This is a critical
    /// change that enables "MCP-only" agents while ensuring every agent has context access and quick reply capabilities.</para>
    /// <para><strong>Tool Separation Strategy:</strong></para>
    /// <para>Uses a custom equality comparer (ToolDefinitionNameComparer) to separate tools by name.
    /// This avoids hardcoding tool names and properly handles record equality based on tool name only.</para>
    /// <list type="number">
    /// <item><term>Base Tools</term><description>
    /// Loaded from morgana.json - typically GetContextVariable, SetContextVariable, SetQuickReplies
    /// </description></item>
    /// <item><term>Intent-Specific Tools</term><description>
    /// All tools from merged array EXCEPT base tools (matched by name using custom comparer)
    /// </description></item>
    /// </list>
    /// <para><strong>Registration Flow:</strong></para>
    /// <code>
    /// 1. Load base tools from morgana.json:
    ///    baseTools = morganaPrompt.GetAdditionalProperty&lt;ToolDefinition[]&gt;("Tools")
    ///
    /// 2. Separate intent-specific tools using custom name-based comparer:
    ///    intentSpecificTools = tools.Except(baseTools, new ToolDefinitionNameComparer())
    ///    // Compares by Name only, avoiding record reference equality issues
    ///
    /// 3. ALWAYS register base tools:
    ///    baseTool = new MorganaTool(logger, () => contextProvider)
    ///    RegisterToolsInAdapter(adapter, baseTool, baseTools)
    ///    → Typically 3 tools registered (GetContextVariable, SetContextVariable, SetQuickReplies)
    ///
    /// 4. IF intent-specific tools exist:
    ///    a. Check if custom MorganaTool implementation exists (via IToolRegistryService)
    ///    b. IF implementation exists:
    ///       customTool = new BillingTool(logger, () => contextProvider)
    ///       RegisterToolsInAdapter(adapter, customTool, intentSpecificTools)
    ///       → N tools registered (GetInvoices, GetInvoiceDetails, etc.)
    ///    c. IF no implementation:
    ///       Log warning about orphaned tool definitions
    ///
    /// 5. Result scenarios:
    ///    Traditional agent: base tools + custom tools
    ///    MCP-only agent: base tools only (+ MCP tools added later)
    ///    Agent with orphaned definitions: base tools only (warning logged)
    /// </code>
    /// <para><strong>Why Custom Comparer:</strong></para>
    /// <para>The default record equality for ToolDefinition compares all properties including the Parameters array.
    /// Even if two ToolDefinitions have the same Name, they may have different Parameter array references,
    /// causing Except() to not exclude them properly. The ToolDefinitionNameComparer ensures we match solely
    /// by Name, avoiding hardcoded tool names and maintaining configuration-driven behavior.</para>
    /// <para><strong>Supported Agent Scenarios:</strong></para>
    /// <code>
    /// SCENARIO A: Traditional Agent with Custom Tools
    /// ===============================================
    /// [HandlesIntent("billing")]
    /// public class BillingAgent : MorganaAgent
    ///
    /// [ProvidesToolForIntent("billing")]
    /// public class BillingTool : MorganaTool
    /// {
    ///     public Task&lt;InvoiceList&gt; GetInvoices(int count) { ... }
    ///     public Task&lt;InvoiceDetails&gt; GetInvoiceDetails(string invoiceId) { ... }
    /// }
    ///
    /// agents.json:
    /// {
    ///   "ID": "Billing",
    ///   "AdditionalProperties": [
    ///     {
    ///       "Tools": [
    ///         { "Name": "GetInvoices", ... },
    ///         { "Name": "GetInvoiceDetails", ... }
    ///       ]
    ///     }
    ///   ]
    /// }
    ///
    /// Log Output:
    /// ✅ Registered 3 base tools for intent 'billing'
    /// ✅ Found custom native tool: BillingTool for intent 'billing' via ToolRegistry
    /// ✅ Registered 2 custom tools for intent 'billing'
    ///
    /// Total: 5 tools available
    ///
    /// SCENARIO B: Modern MCP-Only Agent (NEW SCENARIO SUPPORTED)
    /// ==========================================================
    /// [HandlesIntent("research")]
    /// [UsesMCPServers("brave-search")]
    /// public class ResearchAgent : MorganaAgent
    ///
    /// // NO custom MorganaTool class needed!
    ///
    /// agents.json:
    /// {
    ///   "ID": "Research",
    ///   "AdditionalProperties": [
    ///     {
    ///       "Tools": []  // Empty or omitted - no intent-specific tools
    ///     }
    ///   ]
    /// }
    ///
    /// Log Output:
    /// ✅ Registered 3 base tools for intent 'research'
    /// ℹ️  No intent-specific tools defined for intent 'research' (agent has base tools only)
    /// [Later in RegisterMCPTools]
    /// ✅ Discovered 15 tools from MCP server: brave-search
    /// ✅ Successfully registered 15 MCP tools from brave-search
    ///
    /// Total: 18 tools available (3 base + 15 MCP)
    ///
    /// SCENARIO C: Configuration Mismatch (Warning Case)
    /// =================================================
    /// [HandlesIntent("contract")]
    /// public class ContractAgent : MorganaAgent
    ///
    /// // NO ContractTool implementation exists!
    ///
    /// agents.json:
    /// {
    ///   "ID": "Contract",
    ///   "AdditionalProperties": [
    ///     {
    ///       "Tools": [
    ///         { "Name": "GetContractDetails", ... }  // Defined but not implemented
    ///       ]
    ///     }
    ///   ]
    /// }
    ///
    /// Log Output:
    /// ✅ Registered 3 base tools for intent 'contract'
    /// ⚠️  Intent 'contract' has 1 tool(s) defined in agents.json but no MorganaTool implementation found.
    ///     Tools will be ignored: GetContractDetails
    ///
    /// Total: 3 tools available (base tools only - orphaned definitions ignored)
    /// </code>
    /// <para><strong>Why This Change Matters:</strong></para>
    /// <list type="bullet">
    /// <item><term>Context Access</term><description>Every agent can use GetContextVariable/SetContextVariable for conversation state</description></item>
    /// <item><term>Quick Replies</term><description>Every agent can use SetQuickReplies for user-friendly guided interactions</description></item>
    /// <item><term>MCP-First Architecture</term><description>Agents can rely entirely on external MCP tools without creating empty MorganaTool classes</description></item>
    /// <item><term>Configuration Driven</term><description>No hardcoded tool names - behavior entirely driven by morgana.json configuration</description></item>
    /// <item><term>Backward Compatible</term><description>Existing agents with custom tools work exactly as before</description></item>
    /// <item><term>Clear Warnings</term><description>Orphaned tool definitions (declared but not implemented) are logged clearly</description></item>
    /// </list>
    /// <para><strong>Error Handling:</strong></para>
    /// <para>If custom tool instantiation fails, an exception is thrown with a clear message.
    /// However, base tools are already registered at this point, so the agent would still have
    /// basic capabilities if the error were caught at a higher level (not currently implemented).</para>
    /// </remarks>
    private MorganaToolAdapter CreateToolAdapterForIntent(
        string intent,
        Records.ToolDefinition[] tools,
        MorganaContextProvider contextProvider)
    {
        List<Records.GlobalPolicy> globalPolicies = morganaPrompt.GetAdditionalProperty<List<Records.GlobalPolicy>>("GlobalPolicies");
        MorganaToolAdapter morganaToolAdapter = new MorganaToolAdapter(globalPolicies);

        // Separate base tools (from morgana.json) from intent-specific tools (from agents.json)
        // Use custom comparer to match by Name only, avoiding reference equality issues
        Records.ToolDefinition[] baseTools = morganaPrompt.GetAdditionalProperty<Records.ToolDefinition[]>("Tools");
        Records.ToolDefinition[] intentSpecificTools = tools.Except(baseTools, new ToolDefinitionNameComparer()).ToArray();

        // ALWAYS register base tools (GetContextVariable, SetContextVariable, SetQuickReplies)
        // These are fundamental capabilities every agent needs, regardless of custom tools
        MorganaTool baseTool = new MorganaTool(logger, () => contextProvider);
        RegisterToolsInAdapter(morganaToolAdapter, baseTool, baseTools);
        logger.LogInformation($"Registered {baseTools.Length} base tools for intent '{intent}'");

        // If no intent-specific tools defined, agent has base tools only
        if (intentSpecificTools.Length == 0)
        {
            logger.LogInformation($"No intent-specific tools defined for intent '{intent}' (agent has base tools only)");
            return morganaToolAdapter;
        }

        // Check if a custom native tool exists for this intent
        Type? toolType = toolRegistryService?.FindToolTypeForIntent(intent);
        if (toolType == null)
        {
            logger.LogWarning($"Intent '{intent}' has {intentSpecificTools.Length} tool(s) defined in agents.json but no MorganaTool implementation found. Tools will be ignored: {string.Join(", ", intentSpecificTools.Select(t => t.Name))}");
            return morganaToolAdapter;
        }

        logger.LogInformation($"Found custom native tool: {toolType.Name} for intent '{intent}' via ToolRegistry");

        MorganaTool customToolInstance;
        try
        {
            customToolInstance = (MorganaTool)Activator.CreateInstance(toolType, logger, () => contextProvider)!;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Failed to instantiate custom tool {toolType.Name} for intent '{intent}'");
            throw new InvalidOperationException(
                $"Could not create custom tool instance for intent '{intent}'. " +
                $"Ensure {toolType.Name} has a constructor that accepts (ILogger, Func<MorganaContextProvider>).", ex);
        }

        RegisterToolsInAdapter(morganaToolAdapter, customToolInstance, intentSpecificTools);
        logger.LogInformation($"Registered {intentSpecificTools.Length} custom tools for intent '{intent}'");
        
        return morganaToolAdapter;
    }

    /// <summary>
    /// Custom equality comparer for ToolDefinition that compares only by Name.
    /// Used to properly separate base tools from intent-specific tools (e.g: when using Except())
    /// </summary>
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

        public int GetHashCode(Records.ToolDefinition obj)
        {
            return obj.Name.GetHashCode(StringComparison.OrdinalIgnoreCase);
        }
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
    /// <remarks>
    /// <para><strong>Registration Process:</strong></para>
    /// <list type="number">
    /// <item>For each tool definition, find matching method in toolInstance via reflection</item>
    /// <item>If method not found, log warning and skip (tool declared but not implemented)</item>
    /// <item>Create delegate from method using Expression.GetDelegateType</item>
    /// <item>Register delegate in MorganaToolAdapter via AddTool</item>
    /// </list>
    /// <para><strong>Method Matching:</strong></para>
    /// <para>Method names must exactly match tool definition names (case-sensitive).
    /// For example, tool definition "GetInvoices" must have a corresponding public method "GetInvoices".</para>
    /// <code>
    /// // Tool definition in agents.json
    /// {
    ///   "Name": "GetInvoices",
    ///   "Description": "Retrieves the last N invoices",
    ///   "Parameters": [...]
    /// }
    ///
    /// // Matching method in BillingTool
    /// public async Task&lt;InvoiceList&gt; GetInvoices(int count)
    /// {
    ///     // Implementation
    /// }
    /// </code>
    /// <para><strong>Usage Contexts:</strong></para>
    /// <code>
    /// // Register base tools (ALWAYS)
    /// MorganaTool baseTool = new MorganaTool(logger, () => contextProvider);
    /// RegisterToolsInAdapter(adapter, baseTool, baseTools);
    /// // Registers: GetContextVariable, SetContextVariable, SetQuickReplies
    ///
    /// // Register custom tools (IF custom MorganaTool exists)
    /// BillingTool customTool = new BillingTool(logger, () => contextProvider);
    /// RegisterToolsInAdapter(adapter, customTool, intentSpecificTools);
    /// // Registers: GetInvoices, GetInvoiceDetails, etc.
    /// </code>
    /// <para><strong>Delegate Creation:</strong></para>
    /// <para>Creates strongly-typed delegates that preserve parameter names and types for
    /// AIFunctionFactory, enabling proper LLM tool calling with parameter metadata.</para>
    /// <code>
    /// // Method signature
    /// public async Task&lt;InvoiceList&gt; GetInvoices(int count)
    ///
    /// // Resulting delegate type
    /// Func&lt;int, Task&lt;InvoiceList&gt;&gt;
    /// </code>
    /// <para><strong>Warning Cases:</strong></para>
    /// <list type="bullet">
    /// <item>Tool declared in agents.json but method not found in toolInstance</item>
    /// <item>Method signature doesn't match tool definition parameters</item>
    /// <item>Method is private or protected (GetType().GetMethod only finds public methods)</item>
    /// </list>
    /// </remarks>
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
                logger.LogWarning($"Tool '{toolDefinition.Name}' declared in agents.json but not found in {toolInstance.GetType().Name}");
                continue;
            }

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
    /// Registers MCP (Model Context Protocol) tools for an agent based on [UsesMCPServers] attribute.
    /// Discovers tools from external MCP servers and integrates them into the agent's tool adapter.
    /// </summary>
    /// <param name="agentType">Agent type to check for [UsesMCPServers] attribute</param>
    /// <param name="morganaToolAdapter">Target adapter to register discovered MCP tools into</param>
    /// <remarks>
    /// <para><strong>MCP Integration Flow:</strong></para>
    /// <list type="number">
    /// <item>Check if agent has [UsesMCPServers] attribute</item>
    /// <item>If no attribute or empty ServerNames array, skip (agent doesn't use MCP)</item>
    /// <item>For each declared server name:
    ///   <list type="bullet">
    ///   <item>Get or create MCP client connection via IMCPClientRegistryService</item>
    ///   <item>Discover available tools from server via DiscoverToolsAsync</item>
    ///   <item>Convert MCP tools to Morgana format via MCPToolAdapter</item>
    ///   <item>Register converted tools in MorganaToolAdapter</item>
    ///   </list>
    /// </item>
    /// </list>
    /// <para><strong>Usage Example:</strong></para>
    /// <code>
    /// // Agent declares MCP servers
    /// [HandlesIntent("research")]
    /// [UsesMCPServers("brave-search", "web-scraper")]
    /// public class ResearchAgent : MorganaAgent
    ///
    /// // During CreateAgent, this method:
    /// Agent ResearchAgent declares 2 MCP servers: brave-search, web-scraper
    /// Registering MCP tools from server: brave-search
    /// Discovered 15 tools from MCP server: brave-search
    /// Registered MCP tool: brave_search_query
    /// Registered MCP tool: brave_search_images
    /// ...
    /// Successfully registered 15 MCP tools from brave-search
    /// Registering MCP tools from server: web-scraper
    /// Discovered 3 tools from MCP server: web-scraper
    /// ...
    /// Successfully registered 3 MCP tools from web-scraper
    ///
    /// Total MCP tools: 18 (15 + 3)
    /// Combined with base tools: 21 tools total (3 base + 18 MCP)
    /// </code>
    /// <para><strong>Server Configuration:</strong></para>
    /// <para>MCP servers must be configured in appsettings.json before they can be used:</para>
    /// <code>
    /// {
    ///   "Morgana": {
    ///     "MCPServers": [
    ///       {
    ///         "Name": "brave-search",
    ///         "Uri": "stdio://path/to/brave-search-server",
    ///         "Enabled": true
    ///       }
    ///     ]
    ///   }
    /// }
    /// </code>
    /// <para><strong>Error Handling:</strong></para>
    /// <para>If MCP tool registration fails for a server, the error is logged but doesn't prevent
    /// agent creation. Other servers continue to be processed. This allows partial functionality
    /// if some MCP servers are unavailable.</para>
    /// <para><strong>Design Note:</strong></para>
    /// <para>MCP tool registration happens AFTER base and custom tool registration, so the
    /// agent always has at minimum its base tools even if all MCP servers fail.</para>
    /// </remarks>
    private void RegisterMCPTools(Type agentType, MorganaToolAdapter morganaToolAdapter)
    {
        UsesMCPServersAttribute? attribute = agentType.GetCustomAttribute<UsesMCPServersAttribute>();
        if (attribute == null || attribute.ServerNames.Length == 0)
        {
            logger.LogDebug($"Agent {agentType.Name} does not use MCP servers");
            return;
        }

        logger.LogInformation($"Agent {agentType.Name} declares {attribute.ServerNames.Length} MCP servers: {string.Join(", ", attribute.ServerNames)}");

        foreach (string serverName in attribute.ServerNames)
        {
            try
            {
                RegisterMCPToolsFromServer(serverName, morganaToolAdapter);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, $"Failed to register MCP tools from server: {serverName}");
            }
        }
    }

    /// <summary>
    /// Registers tools from a specific MCP server into the MorganaToolAdapter.
    /// Handles connection, tool discovery, conversion, and registration for a single MCP server.
    /// </summary>
    /// <param name="serverName">Name of the MCP server from configuration (e.g., "brave-search")</param>
    /// <param name="morganaToolAdapter">Target adapter to register discovered tools into</param>
    /// <remarks>
    /// <para><strong>Server Integration Process:</strong></para>
    /// <list type="number">
    /// <item>Get or create MCP client connection (connection pooling via IMCPClientRegistryService)</item>
    /// <item>Discover available tools via client.DiscoverToolsAsync()</item>
    /// <item>If no tools found, log warning and return early</item>
    /// <item>Create MCPToolAdapter to convert MCP tools to Morgana format</item>
    /// <item>For each converted tool:
    ///   <list type="bullet">
    ///   <item>Create ToolDefinition with tool name and parameters</item>
    ///   <item>Register delegate and definition in MorganaToolAdapter</item>
    ///   <item>Log successful registration</item>
    ///   </list>
    /// </item>
    /// </list>
    /// <para><strong>MCP Tool Conversion:</strong></para>
    /// <para>MCPToolAdapter bridges MCP protocol tools to Morgana's tool system:</para>
    /// <code>
    /// // MCP Server Tool (JSON Schema format)
    /// {
    ///   "name": "brave_search_query",
    ///   "description": "Search the web using Brave Search",
    ///   "inputSchema": {
    ///     "type": "object",
    ///     "properties": {
    ///       "query": { "type": "string", "description": "Search query" }
    ///     }
    ///   }
    /// }
    ///
    /// // Converted to Morgana ToolDefinition
    /// {
    ///   "Name": "brave_search_query",
    ///   "Description": "Search the web using Brave Search",
    ///   "Parameters": [
    ///     {
    ///       "Name": "query",
    ///       "Description": "Search query",
    ///       "Required": true,
    ///       "Scope": "request"
    ///     }
    ///   ]
    /// }
    ///
    /// // Delegate created for execution
    /// Func&lt;string, Task&lt;object&gt;&gt; delegate = async (query) =>
    /// {
    ///     return await mcpClient.CallToolAsync("brave_search_query", new { query });
    /// };
    /// </code>
    /// <para><strong>Connection Pooling:</strong></para>
    /// <para>MCP clients are pooled by IMCPClientRegistryService, so multiple agents using the
    /// same server share a single connection:</para>
    /// <code>
    /// [HandlesIntent("research")]
    /// [UsesMCPServers("brave-search")]
    /// public class ResearchAgent : MorganaAgent
    ///
    /// [HandlesIntent("news")]
    /// [UsesMCPServers("brave-search")]
    /// public class NewsAgent : MorganaAgent
    ///
    /// // Both agents share the SAME brave-search connection
    /// // GetOrCreateClientAsync returns cached client on second call
    /// </code>
    /// <para><strong>Logging Output:</strong></para>
    /// <code>
    /// Registering MCP tools from server: brave-search
    /// Discovered 15 tools from MCP server: brave-search
    /// Registered MCP tool: brave_search_query
    /// Registered MCP tool: brave_search_images
    /// Registered MCP tool: brave_get_webpage
    /// ...
    /// Successfully registered 15 MCP tools from brave-search
    /// </code>
    /// <para><strong>Error Scenarios:</strong></para>
    /// <list type="bullet">
    /// <item>Server not configured in appsettings.json → Exception thrown</item>
    /// <item>Server connection fails → Exception logged, caught by RegisterMCPTools</item>
    /// <item>No tools discovered → Warning logged, no tools registered</item>
    /// <item>Individual tool registration fails → Error logged, other tools continue</item>
    /// </list>
    /// </remarks>
    private void RegisterMCPToolsFromServer(string serverName, MorganaToolAdapter morganaToolAdapter)
    {
        logger.LogInformation($"Registering MCP tools from server: {serverName}");

        MCPClient mcpClient = imcpClientRegistryService!.GetOrCreateClientAsync(serverName)
            .GetAwaiter()
            .GetResult();

        IList<ModelContextProtocol.Protocol.Tool> mcpTools = mcpClient.DiscoverToolsAsync().GetAwaiter().GetResult();

        if (mcpTools.Count == 0)
        {
            logger.LogWarning($"No tools discovered from MCP server: {serverName}");
            return;
        }

        logger.LogInformation($"Discovered {mcpTools.Count} tools from MCP server: {serverName}");

        MCPToolAdapter mcpToolAdapter = new MCPToolAdapter(mcpClient, logger);
        Dictionary<string, (Delegate toolDelegate, Records.ToolDefinition toolDefinition)> convertedTools = 
            mcpToolAdapter.ConvertTools(mcpTools.ToList());

        foreach (KeyValuePair<string, (Delegate toolDelegate, Records.ToolDefinition toolDefinition)> kvp in convertedTools)
        {
            try
            {
                Records.ToolDefinition namedToolDefinition = kvp.Value.toolDefinition with { Name = kvp.Key };
                morganaToolAdapter.AddTool(kvp.Key, kvp.Value.toolDelegate, namedToolDefinition);

                logger.LogInformation($"Registered MCP tool: {kvp.Key}");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, $"Failed to register MCP tool: {kvp.Key} from {serverName}");
            }
        }

        logger.LogInformation($"Successfully registered {convertedTools.Count} MCP tools from {serverName}");
    }
}