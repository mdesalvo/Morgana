using System.Reflection;
using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Morgana.Framework.Abstractions;
using Morgana.Framework.Attributes;
using Morgana.Framework.Interfaces;
using Morgana.Framework.Providers;
using Morgana.Framework.Services;

namespace Morgana.Framework.Adapters;

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
    /// Service for creating IChatReducer instances for context window management.
    /// Creates SummarizingChatReducer based on configuration to optimize LLM costs.
    /// </summary>
    protected readonly SummarizingChatReducerService chatReducerService;

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
    /// <param name="chatReducerService">Service for reducing context window sent to LLM</param>
    /// <param name="logger">Logger instance for diagnostics</param>
    public MorganaAgentAdapter(
        IChatClient chatClient,
        IPromptResolverService promptResolverService,
        IToolRegistryService toolRegistryService,
        IMCPClientRegistryService imcpClientRegistryService,
        SummarizingChatReducerService chatReducerService,
        ILogger logger)
    {
        this.chatClient = chatClient;
        this.promptResolverService = promptResolverService;
        this.toolRegistryService = toolRegistryService;
        this.imcpClientRegistryService = imcpClientRegistryService;
        this.chatReducerService = chatReducerService;
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
    /// Optional callback invoked when the agent writes a shared context variable.
    /// Wire to <see cref="MorganaAgent.OnSharedContextUpdate"/> to broadcast updates via the RouterActor.
    /// </param>
    /// <returns>
    /// A tuple of (AIAgent, MorganaAIContextProvider, MorganaChatHistoryProvider) —
    /// all three singletons for this agent instance.
    /// </returns>
    public (AIAgent agent, MorganaAIContextProvider provider, MorganaChatHistoryProvider historyProvider) CreateAgent(
        Type agentType,
        string conversationId,
        Func<AgentSession?> sessionAccessor,
        Action<string, object>? sharedContextCallback = null)
    {
        HandlesIntentAttribute? intentAttribute = agentType.GetCustomAttribute<HandlesIntentAttribute>()
            ?? throw new InvalidOperationException($"Agent type '{agentType.Name}' must be decorated with [HandlesIntent] attribute");
        string intent = intentAttribute.Intent;

        logger.LogInformation($"Creating agent for intent '{intent}'...");

        Records.Prompt agentPrompt = promptResolverService.ResolveAsync(intent).GetAwaiter().GetResult();

        Records.ToolDefinition[] agentTools = [.. morganaPrompt.GetAdditionalProperty<Records.ToolDefinition[]>("Tools")
                                                    .Union(agentPrompt.GetAdditionalProperty<Records.ToolDefinition[]>("Tools"))];

        MorganaAIContextProvider morganaAIContextProvider = CreateAIContextProvider(
            intent,
            agentTools,
            sharedContextCallback);

        // Build the ToolContext factory — evaluated lazily on each tool call so it always
        // captures the in-flight session from the Akka actor.
        Func<MorganaTool.ToolContext> toolContextFactory = () =>
        {
            AgentSession session = sessionAccessor()
                ?? throw new InvalidOperationException(
                    $"Agent '{intent}' has no active session during tool execution. " +
                    $"Ensure ExecuteAgentAsync sets aiAgentSession before invoking the agent.");

            return new MorganaTool.ToolContext(morganaAIContextProvider, session);
        };

        MorganaToolAdapter morganaToolAdapter = CreateToolAdapterForIntent(
            intent,
            agentTools,
            toolContextFactory);

        RegisterMCPTools(agentType, morganaToolAdapter);

        IChatReducer? chatReducer = chatReducerService.CreateReducer(chatClient);

        MorganaChatHistoryProvider chatHistoryProvider = new MorganaChatHistoryProvider(
            intent,
            chatReducer,
            logger);

        AIAgent aiAgent = chatClient.AsAIAgent(
            new ChatClientAgentOptions
            {
                // Give the agent its context providers
                AIContextProviders = [morganaAIContextProvider],

                // Give the agent its history provider
                ChatHistoryProvider = chatHistoryProvider,

                // Give the agent its identifiers
                Id = $"{intent.ToLower()}-{conversationId}",
                Name = intent,

                // Give the agent its instructions and tools
                ChatOptions = new ChatOptions
                {
                    Instructions = ComposeAgentInstructions(agentPrompt),
                    Tools = [.. morganaToolAdapter.CreateAllFunctions()]
                }
            });

        return (aiAgent, morganaAIContextProvider, chatHistoryProvider);
    }

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
    /// Analyzes tool definitions to identify variables that should be broadcast across agents.
    /// </summary>
    /// <param name="agentName">Name of the agent for logging purposes (e.g., "billing")</param>
    /// <param name="tools">Tool definitions to scan for shared variable declarations</param>
    /// <param name="sharedContextCallback">
    /// Optional callback invoked when a shared variable is set.
    /// Wired to agent's OnSharedContextUpdate for broadcasting to RouterActor.
    /// </param>
    /// <returns>Configured MorganaAIContextProvider instance for the agent</returns>
    private MorganaAIContextProvider CreateAIContextProvider(
        string agentName,
        IEnumerable<Records.ToolDefinition> tools,
        Action<string, object>? sharedContextCallback = null)
    {
        List<string> sharedVariables = [.. tools
            .SelectMany(t => t.Parameters)
            .Where(p => p.Shared && string.Equals(p.Scope, "context", StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Name)
            .Distinct()];

        logger.LogInformation(
            sharedVariables.Count > 0
                ? $"Agent '{agentName}' has {sharedVariables.Count} shared variables: {string.Join(", ", sharedVariables)}"
                : $"Agent '{agentName}' has NO shared variables");

        MorganaAIContextProvider aiContextProvider = new MorganaAIContextProvider(logger, sharedVariables);

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
        List<Records.GlobalPolicy> globalPolicies = morganaPrompt.GetAdditionalProperty<List<Records.GlobalPolicy>>("GlobalPolicies");
        MorganaToolAdapter morganaToolAdapter = new MorganaToolAdapter(globalPolicies);

        // Separate base tools (from morgana.json) from intent-specific tools (from agents.json)
        // Use custom comparer to match by Name only, avoiding reference equality issues
        Records.ToolDefinition[] baseTools = morganaPrompt.GetAdditionalProperty<Records.ToolDefinition[]>("Tools");
        Records.ToolDefinition[] intentSpecificTools = tools.Except(baseTools, new ToolDefinitionNameComparer()).ToArray();

        // ALWAYS register base tools (GetContextVariable, SetContextVariable, SetQuickReplies)
        // These are fundamental capabilities every agent needs, regardless of custom tools
        MorganaTool baseTool = new MorganaTool(logger, toolContextFactory);
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
            logger.LogWarning(
                $"Intent '{intent}' has {intentSpecificTools.Length} tool(s) defined in agents.json " +
                $"but no MorganaTool implementation found. Tools will be ignored: " +
                $"{string.Join(", ", intentSpecificTools.Select(t => t.Name))}");
            return morganaToolAdapter;
        }

        logger.LogInformation($"Found custom native tool: {toolType.Name} for intent '{intent}' via ToolRegistry");

        MorganaTool customToolInstance;
        try
        {
            customToolInstance = (MorganaTool)Activator.CreateInstance(toolType, logger, toolContextFactory)!;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Failed to instantiate custom tool {toolType.Name} for intent '{intent}'");
            throw new InvalidOperationException(
                $"Could not create custom tool instance for intent '{intent}'. " +
                $"Ensure {toolType.Name} has a constructor accepting " +
                $"(ILogger, Func<MorganaTool.ToolContext>).", ex);
        }

        RegisterToolsInAdapter(morganaToolAdapter, customToolInstance, intentSpecificTools);
        logger.LogInformation($"Registered {intentSpecificTools.Length} custom tools for intent '{intent}'");

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
    /// </remarks>
    private void RegisterMCPToolsFromServer(string serverName, MorganaToolAdapter morganaToolAdapter)
    {
        logger.LogInformation($"Registering MCP tools from server: {serverName}");

        MCPClient mcpClient = imcpClientRegistryService.GetOrCreateClientAsync(serverName)
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