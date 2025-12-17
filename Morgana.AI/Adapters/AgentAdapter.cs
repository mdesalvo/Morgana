using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Morgana.AI.Agents;
using Morgana.AI.Interfaces;
using Morgana.AI.Tools;
using static Morgana.AI.Records;

namespace Morgana.AI.Adapters;

public class AgentAdapter
{
    private readonly IChatClient chatClient;
    private readonly IPromptResolverService promptResolverService;

    public AgentAdapter(IChatClient chatClient, IPromptResolverService promptResolverService)
    {
        this.chatClient = chatClient;
        this.promptResolverService = promptResolverService;
    }

    public AIAgent CreateBillingAgent()
    {
        BillingTool billingTool = new BillingTool();
        ToolAdapter billingToolAdapter = new ToolAdapter();

        Prompt billingPrompt = promptResolverService.ResolveAsync("Billing")
                                                    .GetAwaiter()
                                                    .GetResult();

        ToolDefinition[]? billingTools = billingPrompt.GetAdditionalProperty<ToolDefinition[]>("Tools");
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
            name: nameof(BillingAgent),
            tools: [.. billingToolAdapter.CreateAllFunctions()]);
    }

    public AIAgent CreateContractAgent()
    {
        ContractTool contractTool = new ContractTool();
        ToolAdapter contractToolAdapter = new ToolAdapter();

        Prompt contractPrompt = promptResolverService.ResolveAsync("Contract")
                                                     .GetAwaiter()
                                                     .GetResult();

        ToolDefinition[]? contractTools = contractPrompt.GetAdditionalProperty<ToolDefinition[]>("Tools");
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
            name: nameof(ContractAgent),
            tools: [.. contractToolAdapter.CreateAllFunctions()]);
    }

    public AIAgent CreateTroubleshootingAgent()
    {
        TroubleshootingTool troubleshootingTool = new TroubleshootingTool();
        ToolAdapter troubleshootingToolAdapter = new ToolAdapter();

        Prompt troubleshootingPrompt = promptResolverService.ResolveAsync("Troubleshooting")
                                                            .GetAwaiter()
                                                            .GetResult();

        ToolDefinition[]? troubleshootingTools = troubleshootingPrompt.GetAdditionalProperty<ToolDefinition[]>("Tools");
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
            name: nameof(TroubleshootingAgent),
            tools: [.. troubleshootingToolAdapter.CreateAllFunctions()]);
    }
}