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
/// Handles agent instantiation, instruction composition, tool registration, context provider setup, and MCP integration.
/// </summary>
public class AgentAdapter
{
    protected readonly IPromptResolverService promptResolverService;
    protected readonly IChatClient chatClient;
    protected readonly ILogger logger;
    protected readonly IToolRegistryService? toolRegistryService;
    protected readonly IMCPClientService? mcpClientService;
    protected readonly Prompt morganaPrompt;

    /// <summary>
    /// Initializes a new instance of the AgentAdapter.
    /// </summary>
    public AgentAdapter(
        IChatClient chatClient,
        IPromptResolverService promptResolverService,
        ILogger logger,
        IToolRegistryService? toolRegistryService = null,
        IMCPClientService? mcpClientService = null)
    {
        this.chatClient = chatClient;
        this.promptResolverService = promptResolverService;
        this.logger = logger;
        this.toolRegistryService = toolRegistryService;
        this.mcpClientService = mcpClientService;

        morganaPrompt = promptResolverService.ResolveAsync("Morgana").GetAwaiter().GetResult();
    }

    private string FormatGlobalPolicies(List<GlobalPolicy> policies)
    {
        StringBuilder sb = new StringBuilder();

        foreach (GlobalPolicy policy in policies.OrderBy(p => p.Type)
                                                .ThenBy(p => p.Priority))
        {
            sb.AppendLine($"{policy.Name}: {policy.Description}");
        }

        return sb.ToString().TrimEnd();
    }

    private string ComposeAgentInstructions(Prompt agentPrompt)
    {
        List<GlobalPolicy> globalPolicies = morganaPrompt.GetAdditionalProperty<List<GlobalPolicy>>("GlobalPolicies");
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

    private List<string> ExtractSharedVariables(IEnumerable<ToolDefinition> tools)
    {
        return tools
            .SelectMany(t => t.Parameters)
            .Where(p => p.Shared && string.Equals(p.Scope, "context", StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Name)
            .Distinct()
            .ToList();
    }

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

    private ToolAdapter CreateToolAdapterForIntent(
        string intent,
        ToolDefinition[] tools,
        MorganaContextProvider contextProvider)
    {
        List<GlobalPolicy> globalPolicies = morganaPrompt.GetAdditionalProperty<List<GlobalPolicy>>("GlobalPolicies");
        ToolAdapter toolAdapter = new ToolAdapter(globalPolicies);

        Type? toolType = toolRegistryService?.FindToolTypeForIntent(intent);
        if (toolType == null)
        {
            logger.LogInformation($"No native tool found for intent '{intent}' (agent has no native capabilities)");
            return toolAdapter;
        }

        logger.LogInformation($"Found native tool: {toolType.Name} for intent '{intent}' via ToolRegistry");

        MorganaTool toolInstance;
        try
        {
            toolInstance = (MorganaTool)Activator.CreateInstance(toolType, logger, () => contextProvider)!;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Failed to instantiate tool {toolType.Name} for intent '{intent}'");
            throw new InvalidOperationException(
                $"Could not create tool instance for intent '{intent}'. " +
                $"Ensure {toolType.Name} has a constructor that accepts (ILogger, Func<MorganaContextProvider>).", ex);
        }

        RegisterToolsInAdapter(toolAdapter, toolInstance, tools);
        return toolAdapter;
    }

    private void RegisterToolsInAdapter(
        ToolAdapter toolAdapter,
        MorganaTool toolInstance,
        ToolDefinition[] tools)
    {
        foreach (ToolDefinition toolDefinition in tools)
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

            toolAdapter.AddTool(toolDefinition.Name, toolImplementation, toolDefinition);
        }
    }

    /// <summary>
    /// Registers MCP tools for an agent based on [UsesMCPServers] attribute.
    /// </summary>
    private void RegisterMCPTools(Type agentType, ToolAdapter toolAdapter)
    {
        if (mcpClientService == null)
        {
            return;
        }

        // Read attribute directly from agent type
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
                RegisterMCPToolsFromServer(serverName, toolAdapter);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, $"Failed to register MCP tools from server: {serverName}");
            }
        }
    }

    /// <summary>
    /// Registers tools from a specific MCP server into the ToolAdapter.
    /// </summary>
    private void RegisterMCPToolsFromServer(string serverName, ToolAdapter toolAdapter)
    {
        logger.LogInformation($"Registering MCP tools from server: {serverName}");

        MCPClient mcpClient = mcpClientService!.GetOrCreateClientAsync(serverName)
            .GetAwaiter()
            .GetResult();

        List<ModelContextProtocol.Protocol.Tool> mcpTools = mcpClient.DiscoverToolsAsync()
            .GetAwaiter()
            .GetResult();

        if (mcpTools.Count == 0)
        {
            logger.LogWarning($"No tools discovered from MCP server: {serverName}");
            return;
        }

        logger.LogInformation($"Discovered {mcpTools.Count} tools from MCP server: {serverName}");

        MCPAdapter mcpAdapter = new MCPAdapter(mcpClient, logger);
        Dictionary<string, (Delegate toolDelegate, ToolDefinition toolDefinition)> convertedTools = 
            mcpAdapter.ConvertTools(mcpTools);

        foreach (KeyValuePair<string, (Delegate toolDelegate, ToolDefinition toolDefinition)> kvp in convertedTools)
        {
            try
            {
                string prefixedName = $"{serverName}_{kvp.Key}";
                ToolDefinition prefixedDef = kvp.Value.toolDefinition with { Name = prefixedName };
                toolAdapter.AddTool(prefixedName, kvp.Value.toolDelegate, prefixedDef);
                logger.LogInformation($"Registered MCP tool: {prefixedName}");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, $"Failed to register MCP tool: {kvp.Key} from {serverName}");
            }
        }

        logger.LogInformation($"Successfully registered {convertedTools.Count} MCP tools from {serverName}");
    }

    /// <summary>
    /// Creates a fully configured AIAgent instance from a Morgana agent type.
    /// </summary>
    public (AIAgent agent, MorganaContextProvider provider) CreateAgent(
        Type agentType,
        Action<string, object>? sharedContextCallback = null)
    {
        HandlesIntentAttribute? intentAttribute = agentType.GetCustomAttribute<HandlesIntentAttribute>();
        if (intentAttribute == null)
            throw new InvalidOperationException($"Agent type '{agentType.Name}' must be decorated with [HandlesIntent] attribute");

        string intent = intentAttribute.Intent;

        logger.LogInformation($"Creating agent for intent '{intent}'...");

        Prompt agentPrompt = promptResolverService.ResolveAsync(intent).GetAwaiter().GetResult();

        ToolDefinition[] agentTools = [.. morganaPrompt.GetAdditionalProperty<ToolDefinition[]>("Tools")
                                            .Union(agentPrompt.GetAdditionalProperty<ToolDefinition[]>("Tools"))];

        MorganaContextProvider contextProvider = CreateContextProvider(intent, agentTools, sharedContextCallback);

        ToolAdapter toolAdapter = CreateToolAdapterForIntent(intent, agentTools, contextProvider);

        // Register MCP tools if service available
        if (mcpClientService != null)
            RegisterMCPTools(agentType, toolAdapter);

        string instructions = ComposeAgentInstructions(agentPrompt);

        AIAgent agent = chatClient.CreateAIAgent(
            instructions: instructions,
            name: intent,
            tools: [.. toolAdapter.CreateAllFunctions()]);

        return (agent, contextProvider);
    }
}