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
public class MorganaAgentAdapter
{
    protected readonly IPromptResolverService promptResolverService;
    protected readonly IChatClient chatClient;
    protected readonly IToolRegistryService toolRegistryService;
    protected readonly IMCPClientRegistryService imcpClientRegistryService;
    protected readonly ILogger logger;
    protected readonly Records.Prompt morganaPrompt;

    /// <summary>
    /// Initializes a new instance of the MorganaAgentAdapter.
    /// </summary>
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

    private MorganaToolAdapter CreateToolAdapterForIntent(
        string intent,
        Records.ToolDefinition[] tools,
        MorganaContextProvider contextProvider)
    {
        List<Records.GlobalPolicy> globalPolicies = morganaPrompt.GetAdditionalProperty<List<Records.GlobalPolicy>>("GlobalPolicies");
        MorganaToolAdapter morganaToolAdapter = new MorganaToolAdapter(globalPolicies);

        Type? toolType = toolRegistryService?.FindToolTypeForIntent(intent);
        if (toolType == null)
        {
            logger.LogInformation($"No native tool found for intent '{intent}' (agent has no native capabilities)");
            return morganaToolAdapter;
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

        RegisterToolsInAdapter(morganaToolAdapter, toolInstance, tools);
        return morganaToolAdapter;
    }

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
    /// Registers MCP tools for an agent based on [UsesMCPServers] attribute.
    /// </summary>
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
    /// </summary>
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