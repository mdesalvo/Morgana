using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Morgana.AI.Agents;
using Morgana.AI.Attributes;
using Morgana.AI.Interfaces;
using Morgana.AI.Tools;
using Morgana.AI.Providers;
using System.Reflection;
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

        // Registra callback per shared context updates
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

        // Crea context provider
        MorganaContextProvider contextProvider = CreateContextProvider(
            billingIntent, 
            billingTools, 
            sharedContextCallback);

        // Crea tool con lazy access al provider
        BillingTool billingTool = new BillingTool(logger, () => contextProvider);

        // Crea tool adapter
        ToolAdapter billingToolAdapter = new ToolAdapter(morganaPrompt);
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

        AIAgent agent = chatClient.CreateAIAgent(
            instructions: $"{morganaPrompt.Content}\n{morganaPrompt.Instructions}\n\n{billingPrompt.Content}\n{billingPrompt.Instructions}",
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

        // Crea context provider
        MorganaContextProvider contextProvider = CreateContextProvider(
            contractIntent, 
            contractTools, 
            sharedContextCallback);

        // Crea tool con lazy access al provider
        ContractTool contractTool = new ContractTool(logger, () => contextProvider);

        // Crea tool adapter
        ToolAdapter contractToolAdapter = new ToolAdapter(morganaPrompt);
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

        AIAgent agent = chatClient.CreateAIAgent(
            instructions: $"{morganaPrompt.Content}\n{morganaPrompt.Instructions}\n\n{contractPrompt.Content}\n{contractPrompt.Instructions}",
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

        // Crea context provider
        MorganaContextProvider contextProvider = CreateContextProvider(
            troubleShootingIntent, 
            troubleshootingTools, 
            sharedContextCallback);

        // Crea tool con lazy access al provider
        TroubleshootingTool troubleshootingTool = new TroubleshootingTool(logger, () => contextProvider);

        // Crea tool adapter
        ToolAdapter troubleshootingToolAdapter = new ToolAdapter(morganaPrompt);
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

        AIAgent agent = chatClient.CreateAIAgent(
            instructions: $"{morganaPrompt.Content}\n{morganaPrompt.Instructions}\n\n{troubleshootingPrompt.Content}\n{troubleshootingPrompt.Instructions}",
            name: troubleShootingIntent,
            tools: [.. troubleshootingToolAdapter.CreateAllFunctions()]);

        return (agent, contextProvider);
    }
}