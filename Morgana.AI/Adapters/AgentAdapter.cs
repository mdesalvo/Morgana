using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Morgana.AI.Agents;
using Morgana.AI.Attributes;
using Morgana.AI.Interfaces;
using Morgana.AI.Tools;
using System.Reflection;
using static Morgana.AI.Records;

namespace Morgana.AI.Adapters;

public class AgentAdapter
{
    protected readonly IPromptResolverService promptResolverService;
    protected readonly IChatClient chatClient;
    private const string ToolDefinitionAccessor = "Tools";

    public AgentAdapter(IChatClient chatClient, IPromptResolverService promptResolverService)
    {
        this.chatClient = chatClient;
        this.promptResolverService = promptResolverService;
    }

    public AIAgent CreateBillingAgent()
    {
        BillingTool billingTool = new BillingTool();
        ToolAdapter billingToolAdapter = new ToolAdapter();

        string billingIntent = typeof(BillingAgent).GetCustomAttribute<HandlesIntentAttribute>()!.Intent;
        Prompt billingPrompt = promptResolverService.ResolveAsync(billingIntent)
                                                    .GetAwaiter()
                                                    .GetResult();

        ToolDefinition[]? billingTools = billingPrompt.GetAdditionalProperty<ToolDefinition[]>(ToolDefinitionAccessor);
        foreach (ToolDefinition billingToolDefinition in billingTools ?? [])
        {
            Delegate billingToolImplementation = billingToolDefinition.Name switch
            {
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

    public AIAgent CreateContractAgent()
    {
        ContractTool contractTool = new ContractTool();
        ToolAdapter contractToolAdapter = new ToolAdapter();

        string contractIntent = typeof(ContractAgent).GetCustomAttribute<HandlesIntentAttribute>()!.Intent;
        Prompt contractPrompt = promptResolverService.ResolveAsync(contractIntent)
                                                     .GetAwaiter()
                                                     .GetResult();

        ToolDefinition[]? contractTools = contractPrompt.GetAdditionalProperty<ToolDefinition[]>(ToolDefinitionAccessor);
        foreach (ToolDefinition contractToolDefinition in contractTools ?? [])
        {
            Delegate contractToolImplementation = contractToolDefinition.Name switch
            {
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

    public AIAgent CreateTroubleshootingAgent()
    {
        TroubleshootingTool troubleshootingTool = new TroubleshootingTool();
        ToolAdapter troubleshootingToolAdapter = new ToolAdapter();

        string troubleShootingIntent = typeof(TroubleshootingAgent).GetCustomAttribute<HandlesIntentAttribute>()!.Intent;
        Prompt troubleshootingPrompt = promptResolverService.ResolveAsync(troubleShootingIntent)
                                                            .GetAwaiter()
                                                            .GetResult();

        ToolDefinition[]? troubleshootingTools = troubleshootingPrompt.GetAdditionalProperty<ToolDefinition[]>(ToolDefinitionAccessor);
        foreach (ToolDefinition troubleshootingToolDefinition in troubleshootingTools ?? [])
        {
            Delegate troubleshootingToolImplementation = troubleshootingToolDefinition.Name switch
            {
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