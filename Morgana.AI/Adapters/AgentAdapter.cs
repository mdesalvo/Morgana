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

    public AgentAdapter(IChatClient chatClient, IPromptResolverService promptResolverService, ILogger<MorganaAgent> logger)
    {
        this.chatClient = chatClient;
        this.promptResolverService = promptResolverService;
        this.logger = logger;

        morganaPrompt = promptResolverService.ResolveAsync("Morgana").GetAwaiter().GetResult();
    }

    public AIAgent CreateBillingAgent(Dictionary<string, object> agentContext)
    {
        BillingTool billingTool = new BillingTool(logger, agentContext);
        ToolAdapter billingToolAdapter = new ToolAdapter(morganaPrompt);

        string billingIntent = typeof(BillingAgent).GetCustomAttribute<HandlesIntentAttribute>()!.Intent;
        Prompt billingPrompt = promptResolverService.ResolveAsync(billingIntent)
                                                    .GetAwaiter()
                                                    .GetResult();

        ToolDefinition[]? billingTools = [.. billingPrompt.GetAdditionalProperty<ToolDefinition[]>("Tools")
                                                .Union(morganaPrompt.GetAdditionalProperty<ToolDefinition[]>("Tools"))];
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
            instructions: $"{billingPrompt.Content}\n{billingPrompt.Instructions}",
            name: billingIntent,
            tools: [.. billingToolAdapter.CreateAllFunctions()]);
    }

    public AIAgent CreateContractAgent(Dictionary<string, object> agentContext)
    {
        ContractTool contractTool = new ContractTool(logger, agentContext);
        ToolAdapter contractToolAdapter = new ToolAdapter(morganaPrompt);

        string contractIntent = typeof(ContractAgent).GetCustomAttribute<HandlesIntentAttribute>()!.Intent;
        Prompt contractPrompt = promptResolverService.ResolveAsync(contractIntent)
                                                     .GetAwaiter()
                                                     .GetResult();

        ToolDefinition[]? contractTools = [.. contractPrompt.GetAdditionalProperty<ToolDefinition[]>("Tools")
                                                .Union(morganaPrompt.GetAdditionalProperty<ToolDefinition[]>("Tools"))];
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
            instructions: $"{contractPrompt.Content}\n{contractPrompt.Instructions}",
            name: contractIntent,
            tools: [.. contractToolAdapter.CreateAllFunctions()]);
    }

    public AIAgent CreateTroubleshootingAgent(Dictionary<string, object> agentContext)
    {
        TroubleshootingTool troubleshootingTool = new TroubleshootingTool(logger, agentContext);
        ToolAdapter troubleshootingToolAdapter = new ToolAdapter(morganaPrompt);

        string troubleShootingIntent = typeof(TroubleshootingAgent).GetCustomAttribute<HandlesIntentAttribute>()!.Intent;
        Prompt troubleshootingPrompt = promptResolverService.ResolveAsync(troubleShootingIntent)
                                                            .GetAwaiter()
                                                            .GetResult();

        ToolDefinition[]? troubleshootingTools = [.. troubleshootingPrompt.GetAdditionalProperty<ToolDefinition[]>("Tools")
                                                       .Union(morganaPrompt.GetAdditionalProperty<ToolDefinition[]>("Tools"))];
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
            instructions: $"{troubleshootingPrompt.Content}\n{troubleshootingPrompt.Instructions}",
            name: troubleShootingIntent,
            tools: [.. troubleshootingToolAdapter.CreateAllFunctions()]);
    }
}