using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Morgana.AI.Attributes;
using Morgana.AI.Interfaces;
using Morgana.AI.Tools;
using Morgana.AI.Providers;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging;
using Morgana.AI.Abstractions;
using static Morgana.AI.Records;

namespace Morgana.AI.Adapters;

public class AgentAdapter
{
    protected readonly IPromptResolverService promptResolverService;
    protected readonly IChatClient chatClient;
    protected readonly ILogger<MorganaAgent> logger;
    protected readonly ILogger<MorganaContextProvider> contextProviderLogger;
    protected readonly IMCPToolProvider? mcpToolProvider;
    protected readonly IMCPServerRegistryService? mcpServerRegistryService;
    protected readonly Prompt morganaPrompt;

    public AgentAdapter(
        IChatClient chatClient,
        IPromptResolverService promptResolverService,
        ILogger<MorganaAgent> logger,
        ILogger<MorganaContextProvider> contextProviderLogger,
        IMCPToolProvider? mcpToolProvider = null,
        IMCPServerRegistryService? mcpServerRegistryService = null)
    {
        this.chatClient = chatClient;
        this.promptResolverService = promptResolverService;
        this.logger = logger;
        this.contextProviderLogger = contextProviderLogger;
        this.mcpToolProvider = mcpToolProvider;
        this.mcpServerRegistryService = mcpServerRegistryService;

        morganaPrompt = promptResolverService.ResolveAsync("Morgana").GetAwaiter().GetResult();
    }

    /// <summary>
    /// Formatta le GlobalPolicies in testo strutturato
    /// </summary>
    private string FormatGlobalPolicies(List<GlobalPolicy> policies)
    {
        StringBuilder sb = new StringBuilder();

        //Order the policies by type (Critical, Operational) then by priority (the lower is the most important)
        foreach (GlobalPolicy policy in policies.OrderBy(p => p.Type)
                                                .ThenBy(p => p.Priority))
        {
            sb.AppendLine($"{policy.Name}: {policy.Description}");
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Compone le istruzioni complete per un agente
    /// </summary>
    private string ComposeAgentInstructions(Prompt agentPrompt)
    {
        List<GlobalPolicy> globalPolicies = morganaPrompt.GetAdditionalProperty<List<GlobalPolicy>>("GlobalPolicies");
        string formattedPolicies = FormatGlobalPolicies(globalPolicies);

        StringBuilder sb = new StringBuilder();
        
        //Morgana

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

        //Agent

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
    /// Estrae i nomi delle variabili shared dai tool definitions
    /// </summary>
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
    /// Crea MorganaContextProvider per gestire lo stato dell'agente
    /// </summary>
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

        MorganaContextProvider provider = new MorganaContextProvider(contextProviderLogger, sharedVariables);

        if (sharedContextCallback != null)
            provider.OnSharedContextUpdate = sharedContextCallback;

        return provider;
    }

    /// <summary>
    /// Crea ToolAdapter specifico per un intent, registrando i tool nativi dell'agent
    /// </summary>
    private ToolAdapter CreateToolAdapterForIntent(
        string intent,
        ToolDefinition[] tools,
        MorganaContextProvider contextProvider)
    {
        List<GlobalPolicy> globalPolicies = morganaPrompt.GetAdditionalProperty<List<GlobalPolicy>>("GlobalPolicies");
        ToolAdapter toolAdapter = new ToolAdapter(globalPolicies);

        // Crea tool instance in base all'intent
        MorganaTool toolInstance = intent.ToLower() switch
        {
            "billing" => new BillingTool(logger, () => contextProvider),
            "contract" => new ContractTool(logger, () => contextProvider),
            "troubleshooting" => new TroubleshootingTool(logger, () => contextProvider),
            _ => throw new InvalidOperationException($"Intent '{intent}' does not have native tools configured")
        };

        // Registra i tool tramite reflection o mapping esplicito
        foreach (ToolDefinition toolDefinition in tools)
        {
            // Cerca metodo per nome nel tool instance
            MethodInfo? method = toolInstance.GetType().GetMethod(toolDefinition.Name);
            if (method == null)
            {
                logger.LogWarning($"Tool '{toolDefinition.Name}' declared in prompts.json but not found in {toolInstance.GetType().Name}");
                continue;
            }

            // Crea delegate dal metodo
            Delegate toolImplementation = Delegate.CreateDelegate(
                System.Linq.Expressions.Expression.GetDelegateType(
                    method.GetParameters().Select(p => p.ParameterType)
                        .Concat([method.ReturnType]).ToArray()),
                toolInstance,
                method);

            toolAdapter.AddTool(toolDefinition.Name, toolImplementation, toolDefinition);
        }

        return toolAdapter;
    }

    /// <summary>
    /// Generic agent creation method - replaces CreateBillingAgent, CreateContractAgent, CreateTroubleshootingAgent.
    /// Automatically loads MCP tools from servers declared via IMCPServerRegistryService.
    /// </summary>
    public (AIAgent agent, MorganaContextProvider provider) CreateAgent(
        Type agentType,
        Action<string, object>? sharedContextCallback = null)
    {
        // Extract intent from attribute
        HandlesIntentAttribute? intentAttribute = agentType.GetCustomAttribute<HandlesIntentAttribute>();
        if (intentAttribute == null)
            throw new InvalidOperationException($"Agent type '{agentType.Name}' must be decorated with [HandlesIntent] attribute");

        string intent = intentAttribute.Intent;

        // Get MCP server names from registry instead of direct reflection
        string[] mcpServerNames = mcpServerRegistryService?.GetServerNamesForAgent(agentType) ?? [];

        logger.LogInformation($"Creating agent for intent '{intent}' with MCP servers: {(mcpServerNames.Length > 0 ? string.Join(", ", mcpServerNames) : "none")}");

        // Load agent prompt
        Prompt agentPrompt = promptResolverService.ResolveAsync(intent)
            .GetAwaiter()
            .GetResult();

        // Merge agent tools with global Morgana tools
        ToolDefinition[] agentTools = [.. morganaPrompt.GetAdditionalProperty<ToolDefinition[]>("Tools")
                                            .Union(agentPrompt.GetAdditionalProperty<ToolDefinition[]>("Tools"))];

        List<ToolDefinition> allToolDefinitions = agentTools.ToList();

        // Load MCP tools ONLY from servers declared in registry
        List<AIFunction> mcpTools = [];
        if (mcpToolProvider != null && mcpServerNames.Length > 0)
        {
            foreach (string serverName in mcpServerNames)
            {
                logger.LogInformation($"Loading MCP tools from server '{serverName}' for agent '{intent}'");

                List<AIFunction> serverTools = mcpToolProvider
                    .LoadToolsFromServerAsync(serverName)
                    .GetAwaiter()
                    .GetResult()
                    ?.ToList() ?? [];

                mcpTools.AddRange(serverTools);

                // Add MCP tool definitions to context provider metadata
                foreach (AIFunction mcpTool in serverTools)
                {
                    ToolParameter[] mcpToolParameters = mcpTool.AdditionalProperties?
                        .Select(kvp => new ToolParameter(
                            Name: kvp.Key,
                            Description: kvp.Value?.ToString() ?? "",
                            Required: true,
                            Scope: "request", //MCP tools are scoped to "request" by design
                            Shared: false))
                        .ToArray() ?? [];

                    allToolDefinitions.Add(new ToolDefinition(
                        mcpTool.Name,
                        mcpTool.Description,
                        mcpToolParameters));
                }
            }

            logger.LogInformation(
                $"Agent '{intent}' loaded {mcpTools.Count} MCP tools from {mcpServerNames.Length} server(s)");
        }

        // Create context provider with all tool definitions (native + MCP)
        MorganaContextProvider contextProvider = CreateContextProvider(
            intent,
            allToolDefinitions,
            sharedContextCallback);

        // Create tool adapter and register native tools
        ToolAdapter toolAdapter = CreateToolAdapterForIntent(intent, agentTools, contextProvider);

        // Compose full agent instructions
        string instructions = ComposeAgentInstructions(agentPrompt);

        // Merge native AIFunctions + MCP AIFunctions
        IEnumerable<AIFunction> allFunctions = toolAdapter
            .CreateAllFunctions()
            .Concat(mcpTools);

        // Create AIAgent with all tools
        AIAgent agent = chatClient.CreateAIAgent(
            instructions: instructions,
            name: intent,
            tools: [.. allFunctions]);

        return (agent, contextProvider);
    }
}