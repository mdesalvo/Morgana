using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Morgana.AI.Agents;
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
    protected readonly Prompt morganaPrompt;

    public AgentAdapter(
        IChatClient chatClient,
        IPromptResolverService promptResolverService,
        ILogger<MorganaAgent> logger,
        ILogger<MorganaContextProvider> contextProviderLogger)
    {
        this.chatClient = chatClient;
        this.promptResolverService = promptResolverService;
        this.logger = logger;
        this.contextProviderLogger = contextProviderLogger;

        morganaPrompt = promptResolverService.ResolveAsync("Morgana").GetAwaiter().GetResult();
    }

    /// <summary>
    /// Formatta le GlobalPolicies in testo strutturato
    /// </summary>
    private string FormatGlobalPolicies(List<GlobalPolicy> policies)
    {
        StringBuilder sb = new StringBuilder();

        foreach (GlobalPolicy policy in policies)
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

        if (sharedVariables.Count > 0)
        {
            logger.LogInformation(
                $"Agent '{agentName}' has {sharedVariables.Count} shared variables: {string.Join(", ", sharedVariables)}");
        }
        else
        {
            logger.LogInformation($"Agent '{agentName}' has NO shared variables");
        }

        MorganaContextProvider provider = new MorganaContextProvider(contextProviderLogger, sharedVariables);

        if (sharedContextCallback != null)
            provider.OnSharedContextUpdate = sharedContextCallback;

        return provider;
    }

    public (AIAgent agent, MorganaContextProvider provider) CreateBillingAgent(
        Action<string, object>? sharedContextCallback = null)
    {
        string billingIntent = typeof(BillingAgent).GetCustomAttribute<HandlesIntentAttribute>()!.Intent;
        Prompt billingPrompt = promptResolverService.ResolveAsync(billingIntent)
                                                    .GetAwaiter()
                                                    .GetResult();

        ToolDefinition[] billingTools = [.. billingPrompt.GetAdditionalProperty<ToolDefinition[]>("Tools")
                                              .Union(morganaPrompt.GetAdditionalProperty<ToolDefinition[]>("Tools"))];

        MorganaContextProvider contextProvider = CreateContextProvider(
            billingIntent, 
            billingTools, 
            sharedContextCallback);

        BillingTool billingTool = new BillingTool(logger, () => contextProvider);

        List<GlobalPolicy> globalPolicies = morganaPrompt.GetAdditionalProperty<List<GlobalPolicy>>("GlobalPolicies");
        
        ToolAdapter billingToolAdapter = new ToolAdapter(globalPolicies);
        foreach (ToolDefinition billingToolDefinition in billingTools ?? [])
        {
            Delegate billingToolImplementation = billingToolDefinition.Name switch
            {
                nameof(BillingTool.GetContextVariable) => billingTool.GetContextVariable,
                nameof(BillingTool.SetContextVariable) => billingTool.SetContextVariable,
                nameof(BillingTool.GetInvoices) => billingTool.GetInvoices,
                nameof(BillingTool.GetInvoiceDetails) => billingTool.GetInvoiceDetails,
                _ => throw new InvalidOperationException($"Tool '{billingToolDefinition.Name}' non supportato")
            };

            billingToolAdapter.AddTool(billingToolDefinition.Name, billingToolImplementation, billingToolDefinition);
        }

        string instructions = ComposeAgentInstructions(billingPrompt);

        AIAgent agent = chatClient.CreateAIAgent(
            instructions: instructions,
            name: billingIntent,
            tools: [.. billingToolAdapter.CreateAllFunctions()]);

        return (agent, contextProvider);
    }

    public (AIAgent agent, MorganaContextProvider provider) CreateContractAgent(
        Action<string, object>? sharedContextCallback = null)
    {
        string contractIntent = typeof(ContractAgent).GetCustomAttribute<HandlesIntentAttribute>()!.Intent;
        Prompt contractPrompt = promptResolverService.ResolveAsync(contractIntent)
                                                     .GetAwaiter()
                                                     .GetResult();

        ToolDefinition[] contractTools = [.. contractPrompt.GetAdditionalProperty<ToolDefinition[]>("Tools")
                                                .Union(morganaPrompt.GetAdditionalProperty<ToolDefinition[]>("Tools"))];

        MorganaContextProvider contextProvider = CreateContextProvider(
            contractIntent, 
            contractTools, 
            sharedContextCallback);

        ContractTool contractTool = new ContractTool(logger, () => contextProvider);

        List<GlobalPolicy> globalPolicies = morganaPrompt.GetAdditionalProperty<List<GlobalPolicy>>("GlobalPolicies");
        
        ToolAdapter contractToolAdapter = new ToolAdapter(globalPolicies);
        foreach (ToolDefinition contractToolDefinition in contractTools ?? [])
        {
            Delegate contractToolImplementation = contractToolDefinition.Name switch
            {
                nameof(ContractTool.GetContextVariable) => contractTool.GetContextVariable,
                nameof(ContractTool.SetContextVariable) => contractTool.SetContextVariable,
                nameof(ContractTool.GetContractDetails) => contractTool.GetContractDetails,
                nameof(ContractTool.InitiateCancellation) => contractTool.InitiateCancellation,
                _ => throw new InvalidOperationException($"Tool '{contractToolDefinition.Name}' non supportato")
            };

            contractToolAdapter.AddTool(contractToolDefinition.Name, contractToolImplementation, contractToolDefinition);
        }

        string instructions = ComposeAgentInstructions(contractPrompt);

        AIAgent agent = chatClient.CreateAIAgent(
            instructions: instructions,
            name: contractIntent,
            tools: [.. contractToolAdapter.CreateAllFunctions()]);

        return (agent, contextProvider);
    }

    public (AIAgent agent, MorganaContextProvider provider) CreateTroubleshootingAgent(
        Action<string, object>? sharedContextCallback = null)
    {
        string troubleShootingIntent = typeof(TroubleshootingAgent).GetCustomAttribute<HandlesIntentAttribute>()!.Intent;
        Prompt troubleshootingPrompt = promptResolverService.ResolveAsync(troubleShootingIntent)
                                                            .GetAwaiter()
                                                            .GetResult();

        ToolDefinition[] troubleshootingTools = [.. troubleshootingPrompt.GetAdditionalProperty<ToolDefinition[]>("Tools")
                                                       .Union(morganaPrompt.GetAdditionalProperty<ToolDefinition[]>("Tools"))];

        MorganaContextProvider contextProvider = CreateContextProvider(
            troubleShootingIntent, 
            troubleshootingTools, 
            sharedContextCallback);

        TroubleshootingTool troubleshootingTool = new TroubleshootingTool(logger, () => contextProvider);

        List<GlobalPolicy> globalPolicies = morganaPrompt.GetAdditionalProperty<List<GlobalPolicy>>("GlobalPolicies");
        
        ToolAdapter troubleshootingToolAdapter = new ToolAdapter(globalPolicies);
        foreach (ToolDefinition troubleshootingToolDefinition in troubleshootingTools ?? [])
        {
            Delegate troubleshootingToolImplementation = troubleshootingToolDefinition.Name switch
            {
                nameof(TroubleshootingTool.GetContextVariable) => troubleshootingTool.GetContextVariable,
                nameof(TroubleshootingTool.SetContextVariable) => troubleshootingTool.SetContextVariable,
                nameof(TroubleshootingTool.RunDiagnostics) => troubleshootingTool.RunDiagnostics,
                nameof(TroubleshootingTool.GetTroubleshootingGuide) => troubleshootingTool.GetTroubleshootingGuide,
                _ => throw new InvalidOperationException($"Tool '{troubleshootingToolDefinition.Name}' non supportato")
            };

            troubleshootingToolAdapter.AddTool(troubleshootingToolDefinition.Name, troubleshootingToolImplementation, troubleshootingToolDefinition);
        }

        string instructions = ComposeAgentInstructions(troubleshootingPrompt);

        AIAgent agent = chatClient.CreateAIAgent(
            instructions: instructions,
            name: troubleShootingIntent,
            tools: [.. troubleshootingToolAdapter.CreateAllFunctions()]);

        return (agent, contextProvider);
    }
}