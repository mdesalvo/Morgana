using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Morgana.AI.Agents;
using Morgana.AI.Interfaces;
using Morgana.AI.Tools;
using System.Reflection;
using System.Text.Json;
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

        ToolMethodRegistry billingToolMethodRegistry = new ToolMethodRegistry()
            .AddTool(nameof(BillingTool.GetInvoices), billingTool.GetInvoices)
            .AddTool(nameof(BillingTool.GetInvoiceDetails), billingTool.GetInvoiceDetails);

        Prompt billingPrompt = promptResolverService.ResolveAsync("Billing")
            .GetAwaiter()
            .GetResult();

        ToolDefinition[]? billingTools = JsonSerializer.Deserialize<ToolDefinition[]>(
            billingPrompt.AdditionalProperties["tools"]); // JSON → DTO

        return chatClient.CreateAIAgent(
            instructions: $"{billingPrompt.Content}\n{billingPrompt.Instructions}",
            name: nameof(BillingAgent),
            tools: [.. billingTools?.Select(t => JsonToolFactory.Create(t, billingToolMethodRegistry.ResolveTool(t.Name))) ?? []]);
    }

    public AIAgent CreateContractAgent()
    {
        ContractTool contractTool = new ContractTool();

        ToolMethodRegistry contractToolMethodRegistry = new ToolMethodRegistry()
            .AddTool(nameof(ContractTool.GetContractDetails), contractTool.GetContractDetails)
            .AddTool(nameof(ContractTool.InitiateCancellation), contractTool.InitiateCancellation);

        Prompt contractPrompt = promptResolverService.ResolveAsync("Contract")
            .GetAwaiter()
            .GetResult();

        ToolDefinition[]? contractTools = JsonSerializer.Deserialize<ToolDefinition[]>(
            contractPrompt.AdditionalProperties["tools"]); // JSON → DTO

        return chatClient.CreateAIAgent(
            instructions: $"{contractPrompt.Content}\n{contractPrompt.Instructions}",
            name: nameof(ContractAgent),
            tools: [.. contractTools?.Select(t => JsonToolFactory.Create(t, contractToolMethodRegistry.ResolveTool(t.Name))) ?? []]);
    }

    public AIAgent CreateTroubleshootingAgent()
    {
        TroubleshootingTool troubleshootingTool = new TroubleshootingTool();

        ToolMethodRegistry troubleshootingToolMethodRegistry = new ToolMethodRegistry()
            .AddTool(nameof(TroubleshootingTool.RunDiagnostics), troubleshootingTool.RunDiagnostics)
            .AddTool(nameof(TroubleshootingTool.GetTroubleshootingGuide), troubleshootingTool.GetTroubleshootingGuide);

        Prompt troubleshootingPrompt = promptResolverService.ResolveAsync("Troubleshooting")
            .GetAwaiter()
            .GetResult();

        ToolDefinition[]? troubleshootingTools = JsonSerializer.Deserialize<ToolDefinition[]>(
            troubleshootingPrompt.AdditionalProperties["tools"]); // JSON → DTO

        return chatClient.CreateAIAgent(
            instructions: $"{troubleshootingPrompt.Content}\n{troubleshootingPrompt.Instructions}",
            name: nameof(TroubleshootingAgent),
            tools: [.. troubleshootingTools?.Select(t => JsonToolFactory.Create(t, troubleshootingToolMethodRegistry.ResolveTool(t.Name))) ?? []]);
    }
}

public static class JsonToolFactory
{
    public static AIFunction Create(ToolDefinition tool, Delegate implementation)
    {
        MethodInfo methodInfo = implementation.Method;

        var definition = new AIFunctionDefinition(
            name: tool.Name,
            description: tool.Description,
            parameters: tool.Parameters
                            .Select(p =>
                            {
                                ParameterInfo clrParameter = methodInfo.GetParameters().Single(x => x.Name == p.Name);
                                return new AIFunctionParameterDefinition(
                                    name: p.Name,
                                    type: clrParameter.ParameterType,
                                    description: p.Description,
                                    isRequired: p.Required
                                );
                            }).ToList()
        );

        return AIFunctionFactory.Create(definition, implementation);
    }
}

public class ToolMethodRegistry
{
    private readonly Dictionary<string, Delegate> toolMethods = [];

    public ToolMethodRegistry AddTool<TDelegate>(string toolName, TDelegate toolMethod)
    {
        if (toolMethod is not Delegate del)
            throw new ArgumentException("toolMethod deve essere un delegate", nameof(toolMethod));

        return toolMethods.TryAdd(toolName, del)
            ? this
            : throw new InvalidOperationException($"Tool '{toolName}' non inserito");
    }

    public Delegate ResolveTool(string toolName)
        => toolMethods.TryGetValue(toolName, out var method)
            ? method
            : throw new InvalidOperationException($"Tool '{toolName}' non registrato");
}