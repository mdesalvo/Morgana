using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Morgana.AI.Agents;
using Morgana.AI.Attributes;
using Morgana.AI.Interfaces;
using Morgana.AI.Tools;
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
    protected readonly Prompt morganaPrompt;

    public AgentAdapter(
        IChatClient chatClient,
        IPromptResolverService promptResolverService,
        ILogger<MorganaAgent> logger)
    {
        this.chatClient = chatClient;
        this.promptResolverService = promptResolverService;
        this.logger = logger;

        morganaPrompt = promptResolverService.ResolveAsync("Morgana").GetAwaiter().GetResult();
    }

    // NUOVO: Helper per estrarre variabili shared da ToolDefinition
    private List<string> ExtractSharedVariables(IEnumerable<ToolDefinition> tools)
    {
        return tools
            .SelectMany(t => t.Parameters)
            .Where(p => p.Shared && string.Equals(p.Scope, "context", StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Name)
            .Distinct()
            .ToList();
    }

    // NUOVO: Helper per logging delle shared variables estratte
    private void LogSharedVariables(string agentName, List<string> sharedVariables)
    {
        if (sharedVariables.Count > 0)
        {
            logger.LogInformation($"Agent '{agentName}' has {sharedVariables.Count} shared variables: {string.Join(", ", sharedVariables)}");
        }
        else
        {
            logger.LogInformation($"Agent '{agentName}' has NO shared variables");
        }
    }

    public AIAgent CreateBillingAgent(
        Dictionary<string, object> agentContext,
        Action<string, object>? sharedContextCallback = null)
    {
        string billingIntent = typeof(BillingAgent).GetCustomAttribute<HandlesIntentAttribute>()!.Intent;
        Prompt billingPrompt = promptResolverService.ResolveAsync(billingIntent)
                                                    .GetAwaiter()
                                                    .GetResult();

        ToolDefinition[] billingTools = [.. billingPrompt.GetAdditionalProperty<ToolDefinition[]>("Tools")
                                              .Union(morganaPrompt.GetAdditionalProperty<ToolDefinition[]>("Tools"))];

        // Estrai variabili shared
        List<string> sharedVariables = ExtractSharedVariables(billingTools);
        LogSharedVariables(billingIntent, sharedVariables);

        BillingTool billingTool = new BillingTool(logger, agentContext, sharedVariables);
        
        // NUOVO: registra callback se fornito
        if (sharedContextCallback != null)
            billingTool.RegisterSharedContextUpdateCallback(sharedContextCallback);

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

        return chatClient.CreateAIAgent(
            instructions: $"{morganaPrompt.Content}\n{morganaPrompt.Instructions}\n\n{billingPrompt.Content}\n{billingPrompt.Instructions}",
            name: billingIntent,
            tools: [.. billingToolAdapter.CreateAllFunctions()]);
    }

    public AIAgent CreateContractAgent(
        Dictionary<string, object> agentContext,
        Action<string, object>? sharedContextCallback = null)  // NUOVO parametro
    {
        string contractIntent = typeof(ContractAgent).GetCustomAttribute<HandlesIntentAttribute>()!.Intent;
        Prompt contractPrompt = promptResolverService.ResolveAsync(contractIntent)
                                                     .GetAwaiter()
                                                     .GetResult();

        ToolDefinition[] contractTools = [.. contractPrompt.GetAdditionalProperty<ToolDefinition[]>("Tools")
                                                .Union(morganaPrompt.GetAdditionalProperty<ToolDefinition[]>("Tools"))];

        // Estrai variabili shared
        IEnumerable<string> sharedVariables = ExtractSharedVariables(contractTools);

        ContractTool contractTool = new ContractTool(logger, agentContext, sharedVariables);
        
        // NUOVO: registra callback se fornito
        if (sharedContextCallback != null)
            contractTool.RegisterSharedContextUpdateCallback(sharedContextCallback);

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

        return chatClient.CreateAIAgent(
            instructions: $"{morganaPrompt.Content}\n{morganaPrompt.Instructions}\n\n{contractPrompt.Content}\n{contractPrompt.Instructions}",
            name: contractIntent,
            tools: [.. contractToolAdapter.CreateAllFunctions()]);
    }

    public AIAgent CreateTroubleshootingAgent(
        Dictionary<string, object> agentContext,
        Action<string, object>? sharedContextCallback = null)  // NUOVO parametro
    {
        string troubleShootingIntent = typeof(TroubleshootingAgent).GetCustomAttribute<HandlesIntentAttribute>()!.Intent;
        Prompt troubleshootingPrompt = promptResolverService.ResolveAsync(troubleShootingIntent)
                                                            .GetAwaiter()
                                                            .GetResult();

        ToolDefinition[] troubleshootingTools = [.. troubleshootingPrompt.GetAdditionalProperty<ToolDefinition[]>("Tools")
                                                       .Union(morganaPrompt.GetAdditionalProperty<ToolDefinition[]>("Tools"))];

        // Estrai variabili shared
        IEnumerable<string> sharedVariables = ExtractSharedVariables(troubleshootingTools);

        TroubleshootingTool troubleshootingTool = new TroubleshootingTool(logger, agentContext, sharedVariables);
        
        // NUOVO: registra callback se fornito
        if (sharedContextCallback != null)
            troubleshootingTool.RegisterSharedContextUpdateCallback(sharedContextCallback);

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

        return chatClient.CreateAIAgent(
            instructions: $"{morganaPrompt.Content}\n{morganaPrompt.Instructions}\n\n{troubleshootingPrompt.Content}\n{troubleshootingPrompt.Instructions}",
            name: troubleShootingIntent,
            tools: [.. troubleshootingToolAdapter.CreateAllFunctions()]);
    }
}