using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Morgana.AI.Attributes;
using Morgana.AI.Interfaces;
using Morgana.AI.Providers;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging;
using Morgana.AI.Abstractions;
using static Morgana.AI.Records;

namespace Morgana.AI.Adapters;

/// <summary>
/// Adapter for creating and configuring AIAgent instances from Morgana agent definitions.
/// Handles agent instantiation, instruction composition, tool registration, and context provider setup.
/// </summary>
/// <remarks>
/// <para><strong>Purpose:</strong></para>
/// <para>The AgentAdapter is the factory that transforms declarative agent configurations (from agents.json)
/// into fully functional AIAgent instances with tools, context management, and proper instruction sets.</para>
/// <para><strong>Key Responsibilities:</strong></para>
/// <list type="bullet">
/// <item><term>Agent Creation</term><description>Instantiates AIAgent with proper configuration</description></item>
/// <item><term>Instruction Composition</term><description>Merges Morgana framework instructions with agent-specific prompts</description></item>
/// <item><term>Tool Registration</term><description>Discovers and registers tools for the agent's intent</description></item>
/// <item><term>Context Provider Setup</term><description>Creates MorganaContextProvider with shared variable configuration</description></item>
/// <item><term>Policy Enforcement</term><description>Applies global policies to tool parameters</description></item>
/// </list>
/// <para><strong>Creation Flow:</strong></para>
/// <code>
/// 1. AgentAdapter.CreateAgent(typeof(BillingAgent))
/// 2. Extract intent from [HandlesIntent] attribute
/// 3. Load agent prompt from configuration (agents.json)
/// 4. Merge Morgana + Agent tools
/// 5. Create MorganaContextProvider with shared variables
/// 6. Create ToolAdapter and register native tools
/// 7. Compose full instruction set (Morgana + Agent)
/// 8. Create AIAgent with instructions and tools
/// 9. Return (agent, contextProvider) tuple
/// </code>
/// </remarks>
public class AgentAdapter
{
    protected readonly IPromptResolverService promptResolverService;
    protected readonly IChatClient chatClient;
    protected readonly ILogger logger;
    protected readonly IToolRegistryService? toolRegistryService;
    protected readonly Prompt morganaPrompt;

    /// <summary>
    /// Initializes a new instance of the AgentAdapter.
    /// Loads the Morgana framework prompt on construction for reuse across agent creation.
    /// </summary>
    /// <param name="chatClient">Chat client for LLM interactions (Anthropic, Azure OpenAI, etc.)</param>
    /// <param name="promptResolverService">Service for loading prompt templates from configuration</param>
    /// <param name="logger">Logger instance for adapter diagnostics</param>
    /// <param name="toolRegistryService">Optional tool registry for discovering intent-specific tools</param>
    public AgentAdapter(
        IChatClient chatClient,
        IPromptResolverService promptResolverService,
        ILogger logger,
        IToolRegistryService? toolRegistryService = null)
    {
        this.chatClient = chatClient;
        this.promptResolverService = promptResolverService;
        this.logger = logger;
        this.toolRegistryService = toolRegistryService;

        morganaPrompt = promptResolverService.ResolveAsync("Morgana").GetAwaiter().GetResult();
    }

    /// <summary>
    /// Formats global policies from Morgana configuration into structured text.
    /// Policies are ordered by type (Critical, Operational) and priority (lower = higher priority).
    /// </summary>
    /// <param name="policies">List of global policies from Morgana prompt configuration</param>
    /// <returns>Formatted policy text for inclusion in agent instructions</returns>
    /// <remarks>
    /// <para><strong>Policy Types:</strong></para>
    /// <list type="bullet">
    /// <item><term>Critical</term><description>Must be followed at all times (e.g., ContextHandling rule)</description></item>
    /// <item><term>Operational</term><description>Operational guidelines (e.g., InteractiveToken rule)</description></item>
    /// </list>
    /// <para>Policies are embedded in the agent's system prompt to ensure compliance during LLM interactions.</para>
    /// </remarks>
    private string FormatGlobalPolicies(List<GlobalPolicy> policies)
    {
        StringBuilder sb = new StringBuilder();

        // Order the policies by type (Critical, Operational) then by priority (lower is more important)
        foreach (GlobalPolicy policy in policies.OrderBy(p => p.Type)
                                                .ThenBy(p => p.Priority))
        {
            sb.AppendLine($"{policy.Name}: {policy.Description}");
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Composes the complete instruction set for an agent by merging Morgana framework instructions with agent-specific prompts.
    /// </summary>
    /// <param name="agentPrompt">Agent-specific prompt from configuration (e.g., Billing agent prompt)</param>
    /// <returns>Complete instruction string for AIAgent creation</returns>
    /// <remarks>
    /// <para><strong>Instruction Hierarchy:</strong></para>
    /// <code>
    /// 1. Morgana Content (role definition)
    /// 2. Morgana Personality (witch persona)
    /// 3. Global Policies (critical rules)
    /// 4. Morgana Instructions (general guidelines)
    /// 5. Agent Content (specific capabilities)
    /// 6. Agent Personality (agent-specific tone)
    /// 7. Agent Instructions (agent-specific rules)
    /// </code>
    /// <para>This structure ensures agents inherit Morgana's core behavior while adding specialized capabilities.</para>
    /// </remarks>
    private string ComposeAgentInstructions(Prompt agentPrompt)
    {
        List<GlobalPolicy> globalPolicies = morganaPrompt.GetAdditionalProperty<List<GlobalPolicy>>("GlobalPolicies");
        string formattedPolicies = FormatGlobalPolicies(globalPolicies);

        StringBuilder sb = new StringBuilder();

        // Morgana framework instructions

        sb.AppendLine(morganaPrompt.Content);
        if (!string.IsNullOrEmpty(morganaPrompt.Personality))
        {
            sb.AppendLine(morganaPrompt.Personality);
            sb.AppendLine();
        }
        sb.AppendLine(formattedPolicies);
        sb.AppendLine();
        sb.AppendLine(morganaPrompt.Instructions);
        sb.AppendLine();

        // Agent-specific instructions

        sb.AppendLine(agentPrompt.Content);
        if (!string.IsNullOrEmpty(agentPrompt.Personality))
        {
            sb.AppendLine(agentPrompt.Personality);
            sb.AppendLine();
        }
        sb.Append(agentPrompt.Instructions);

        return sb.ToString();
    }

    /// <summary>
    /// Extracts the names of shared variables from tool definitions.
    /// Shared variables are broadcast to other agents for cross-agent coordination.
    /// </summary>
    /// <param name="tools">Tool definitions from agent configuration</param>
    /// <returns>List of shared variable names (e.g., ["userId", "accountId"])</returns>
    /// <remarks>
    /// <para><strong>Shared Variable Criteria:</strong></para>
    /// <para>A variable is considered shared if:</para>
    /// <list type="bullet">
    /// <item>Scope = "context" (not "request")</item>
    /// <item>Shared = true</item>
    /// </list>
    /// <para><strong>Example from agents.json:</strong></para>
    /// <code>
    /// {
    ///   "Name": "userId",
    ///   "Required": true,
    ///   "Scope": "context",
    ///   "Shared": true  // This variable will be broadcast to other agents
    /// }
    /// </code>
    /// </remarks>
    private List<string> ExtractSharedVariables(IEnumerable<ToolDefinition> tools)
    {
        return tools
            .SelectMany(t => t.Parameters)
            .Where(p => p.Shared && string.Equals(p.Scope, "context", StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Name)
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// Creates a MorganaContextProvider to manage agent state and context variables.
    /// Configures shared variable tracking and optional broadcast callback.
    /// </summary>
    /// <param name="agentName">Name of the agent (for logging)</param>
    /// <param name="tools">Tool definitions containing shared variable configuration</param>
    /// <param name="sharedContextCallback">Optional callback invoked when shared variables are set</param>
    /// <returns>Configured MorganaContextProvider instance</returns>
    /// <remarks>
    /// <para><strong>Context Provider Responsibilities:</strong></para>
    /// <list type="bullet">
    /// <item>Store local and shared context variables</item>
    /// <item>Detect when shared variables are set</item>
    /// <item>Invoke callback to trigger broadcast to other agents</item>
    /// <item>Merge incoming shared context from other agents (first-write-wins)</item>
    /// </list>
    /// <para><strong>Callback Pattern:</strong></para>
    /// <para>The callback is typically set to the agent's OnSharedContextUpdate method,
    /// which broadcasts the variable to the RouterActor for distribution to other agents.</para>
    /// </remarks>
    private MorganaContextProvider CreateContextProvider(
        string agentName,
        IEnumerable<ToolDefinition> tools,
        Action<string, object>? sharedContextCallback = null)
    {
        List<string> sharedVariables = ExtractSharedVariables(tools);

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
    /// Creates a ToolAdapter specific to an intent, discovering and registering native tools.
    /// Uses ToolRegistryService to find the tool type decorated with [ProvidesToolForIntent].
    /// </summary>
    /// <param name="intent">Intent name (e.g., "billing", "contract")</param>
    /// <param name="tools">Tool definitions from configuration</param>
    /// <param name="contextProvider">Context provider for tool access to variables</param>
    /// <returns>Configured ToolAdapter with registered tools</returns>
    /// <remarks>
    /// <para><strong>Tool Discovery Flow:</strong></para>
    /// <list type="number">
    /// <item>Query ToolRegistryService for tool type matching intent</item>
    /// <item>Instantiate tool via Activator with (ILogger, Func&lt;MorganaContextProvider&gt;)</item>
    /// <item>Use reflection to find methods matching tool definitions</item>
    /// <item>Create delegates and register in ToolAdapter</item>
    /// <item>Apply global policies to tool parameter descriptions</item>
    /// </list>
    /// <para><strong>Example:</strong></para>
    /// <code>
    /// [ProvidesToolForIntent("billing")]
    /// public class BillingTool : MorganaTool
    /// {
    ///     public Task&lt;InvoiceList&gt; GetInvoices(int count) { ... }
    ///     public Task&lt;InvoiceDetails&gt; GetInvoiceDetails(string invoiceId) { ... }
    /// }
    /// </code>
    /// </remarks>
    private ToolAdapter CreateToolAdapterForIntent(
        string intent,
        ToolDefinition[] tools,
        MorganaContextProvider contextProvider)
    {
        List<GlobalPolicy> globalPolicies = morganaPrompt.GetAdditionalProperty<List<GlobalPolicy>>("GlobalPolicies");
        ToolAdapter toolAdapter = new ToolAdapter(globalPolicies);

        // Use ToolRegistryService to find tool type
        Type? toolType = toolRegistryService?.FindToolTypeForIntent(intent);
        if (toolType == null)
        {
            // No native tool found for this intent
            logger.LogInformation($"No native tool found for intent '{intent}' (agent has no capabilities!)");
            return toolAdapter;
        }

        logger.LogInformation($"Found native tool: {toolType.Name} for intent '{intent}' via ToolRegistry");

        // Instantiate tool via Activator
        MorganaTool toolInstance;
        try
        {
            toolInstance = (MorganaTool)Activator.CreateInstance(
                toolType,
                logger,
                (Func<MorganaContextProvider>)(() => contextProvider))!;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Failed to instantiate tool {toolType.Name} for intent '{intent}'");
            throw new InvalidOperationException(
                $"Could not create tool instance for intent '{intent}'. " +
                $"Ensure {toolType.Name} has a constructor that accepts (ILogger, Func<MorganaContextProvider>).", ex);
        }

        // Register tools using existing logic
        RegisterToolsInAdapter(toolAdapter, toolInstance, tools);
        return toolAdapter;
    }

    /// <summary>
    /// Registers tools in the ToolAdapter via reflection, creating delegates from tool methods.
    /// Matches tool definitions from configuration with actual methods on the tool instance.
    /// </summary>
    /// <param name="toolAdapter">ToolAdapter to register tools in</param>
    /// <param name="toolInstance">Instance of MorganaTool containing tool implementations</param>
    /// <param name="tools">Tool definitions from configuration</param>
    /// <remarks>
    /// <para><strong>Registration Process:</strong></para>
    /// <list type="number">
    /// <item>For each tool definition, find matching method by name</item>
    /// <item>Validate method exists (log warning if missing)</item>
    /// <item>Create delegate from method using reflection</item>
    /// <item>Register delegate in ToolAdapter with tool definition</item>
    /// </list>
    /// <para><strong>Method Matching:</strong></para>
    /// <para>Tool definition names in agents.json must exactly match method names on the tool class.
    /// Case-sensitive matching is used.</para>
    /// </remarks>
    private void RegisterToolsInAdapter(
        ToolAdapter toolAdapter,
        MorganaTool toolInstance,
        ToolDefinition[] tools)
    {
        // Register tools via reflection or explicit mapping
        foreach (ToolDefinition toolDefinition in tools)
        {
            // Find method by name in tool instance
            MethodInfo? method = toolInstance.GetType().GetMethod(toolDefinition.Name);
            if (method == null)
            {
                logger.LogWarning($"Tool '{toolDefinition.Name}' declared in agents.json but not found in {toolInstance.GetType().Name}");
                continue;
            }

            // Create delegate from method
            Delegate toolImplementation = Delegate.CreateDelegate(
                System.Linq.Expressions.Expression.GetDelegateType(
                    method.GetParameters().Select(p => p.ParameterType)
                        .Concat([method.ReturnType]).ToArray()),
                toolInstance,
                method);

            toolAdapter.AddTool(toolDefinition.Name, toolImplementation, toolDefinition);
        }
    }

    /// <summary>
    /// Creates a fully configured AIAgent instance from a Morgana agent type.
    /// Handles the complete agent creation flow: prompt resolution, tool registration, context setup, and AIAgent instantiation.
    /// </summary>
    /// <param name="agentType">Type of the agent class (must be decorated with [HandlesIntent])</param>
    /// <param name="sharedContextCallback">Optional callback for broadcasting shared context variables</param>
    /// <returns>Tuple of (AIAgent instance, MorganaContextProvider instance)</returns>
    /// <exception cref="InvalidOperationException">Thrown if agent type lacks [HandlesIntent] attribute</exception>
    /// <remarks>
    /// <para><strong>Complete Creation Flow:</strong></para>
    /// <list type="number">
    /// <item>Extract intent from [HandlesIntent] attribute</item>
    /// <item>Load agent prompt from configuration (agents.json)</item>
    /// <item>Merge Morgana framework tools with agent-specific tools</item>
    /// <item>Create MorganaContextProvider with shared variable configuration</item>
    /// <item>Create ToolAdapter and register discovered native tools</item>
    /// <item>Compose full instruction set (Morgana + Agent prompts)</item>
    /// <item>Create AIAgent with instructions and all registered tools</item>
    /// <item>Return agent and context provider for actor use</item>
    /// </list>
    /// <para><strong>Usage by Actors:</strong></para>
    /// <code>
    /// [HandlesIntent("billing")]
    /// public class BillingAgent : MorganaAgent
    /// {
    ///     public BillingAgent(...) : base(...)
    ///     {
    ///         AgentAdapter adapter = sp.GetRequiredService&lt;AgentAdapter&gt;();
    ///         (aiAgent, contextProvider) = adapter.CreateAgent(
    ///             GetType(), 
    ///             OnSharedContextUpdate
    ///         );
    ///     }
    /// }
    /// </code>
    /// </remarks>
    public (AIAgent agent, MorganaContextProvider provider) CreateAgent(
        Type agentType,
        Action<string, object>? sharedContextCallback = null)
    {
        // Extract intent from agent's attribute
        HandlesIntentAttribute? intentAttribute = agentType.GetCustomAttribute<HandlesIntentAttribute>();
        if (intentAttribute == null)
            throw new InvalidOperationException($"Agent type '{agentType.Name}' must be decorated with [HandlesIntent] attribute");

        string intent = intentAttribute.Intent;

        logger.LogInformation($"Creating agent for intent '{intent}'...");

        // Load agent prompt
        Prompt agentPrompt = promptResolverService.ResolveAsync(intent)
            .GetAwaiter()
            .GetResult();

        // Merge Agent tools with Morgana tools for context
        ToolDefinition[] agentTools = [.. morganaPrompt.GetAdditionalProperty<ToolDefinition[]>("Tools")
                                            .Union(agentPrompt.GetAdditionalProperty<ToolDefinition[]>("Tools"))];

        // Create context provider with tool definitions
        MorganaContextProvider contextProvider = CreateContextProvider(
            intent,
            agentTools,
            sharedContextCallback);

        // Create tool adapter and register native tools
        ToolAdapter toolAdapter = CreateToolAdapterForIntent(intent, agentTools, contextProvider);

        // Compose full agent instructions
        string instructions = ComposeAgentInstructions(agentPrompt);

        // Create AIAgent with all tools
        AIAgent agent = chatClient.CreateAIAgent(
            instructions: instructions,
            name: intent,
            tools: [.. toolAdapter.CreateAllFunctions()]);

        return (agent, contextProvider);
    }
}