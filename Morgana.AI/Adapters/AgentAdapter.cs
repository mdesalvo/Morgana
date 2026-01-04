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
    protected readonly ILogger logger;
    protected readonly IToolRegistryService? toolRegistryService;
    protected readonly Prompt morganaPrompt;

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

        MorganaContextProvider provider = new MorganaContextProvider(logger, sharedVariables);

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
    /// Registra i tool nel ToolAdapter tramite reflection
    /// </summary>
    private void RegisterToolsInAdapter(
        ToolAdapter toolAdapter,
        MorganaTool toolInstance,
        ToolDefinition[] tools)
    {
        // Registra i tool tramite reflection o mapping esplicito
        foreach (ToolDefinition toolDefinition in tools)
        {
            // Cerca metodo per nome nel tool instance
            MethodInfo? method = toolInstance.GetType().GetMethod(toolDefinition.Name);
            if (method == null)
            {
                logger.LogWarning($"Tool '{toolDefinition.Name}' declared in morgana.json but not found in {toolInstance.GetType().Name}");
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
    }

    /// <summary>
    /// Generic agent creation method.
    /// </summary>
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