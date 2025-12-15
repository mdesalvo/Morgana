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
        ToolMethodRegistry billingToolRegistry = new ToolMethodRegistry();

        Prompt billingPrompt = promptResolverService.ResolveAsync("Billing")
                                                    .GetAwaiter()
                                                    .GetResult();

        ToolDefinition[]? billingTools = JsonSerializer.Deserialize<ToolDefinition[]>(
            billingPrompt.AdditionalProperties["tools"]);
        foreach (ToolDefinition billingToolDefinition in billingTools ?? [])
        {
            Delegate billingToolImplementation = billingToolDefinition.Name switch
            {
                nameof(BillingTool.GetInvoices) => billingTool.GetInvoices,
                nameof(BillingTool.GetInvoiceDetails) => billingTool.GetInvoiceDetails,
                _ => throw new InvalidOperationException($"Tool '{billingToolDefinition.Name}' non supportato")
            };

            billingToolRegistry.AddTool(billingToolDefinition.Name, billingToolImplementation, billingToolDefinition);
        }

        return chatClient.CreateAIAgent(
            instructions: $"{billingPrompt.Content}\n{billingPrompt.Instructions}",
            name: nameof(BillingAgent),
            tools: [.. billingToolRegistry.CreateAllFunctions()]);
    }

    public AIAgent CreateContractAgent()
    {
        ContractTool contractTool = new ContractTool();
        ToolMethodRegistry contractToolRegistry = new ToolMethodRegistry();

        Prompt contractPrompt = promptResolverService.ResolveAsync("Contract")
                                                     .GetAwaiter()
                                                     .GetResult();

        ToolDefinition[]? contractTools = JsonSerializer.Deserialize<ToolDefinition[]>(
            contractPrompt.AdditionalProperties["tools"]);
        foreach (ToolDefinition contractToolDefinition in contractTools ?? [])
        {
            Delegate contractToolImplementation = contractToolDefinition.Name switch
            {
                nameof(ContractTool.GetContractDetails) => contractTool.GetContractDetails,
                nameof(ContractTool.InitiateCancellation) => contractTool.InitiateCancellation,
                _ => throw new InvalidOperationException($"Tool '{contractToolDefinition.Name}' non supportato")
            };

            contractToolRegistry.AddTool(contractToolDefinition.Name, contractToolImplementation, contractToolDefinition);
        }

        return chatClient.CreateAIAgent(
            instructions: $"{contractPrompt.Content}\n{contractPrompt.Instructions}",
            name: nameof(ContractAgent),
            tools: [.. contractToolRegistry.CreateAllFunctions()]);
    }

    public AIAgent CreateTroubleshootingAgent()
    {
        TroubleshootingTool troubleshootingTool = new TroubleshootingTool();
        ToolMethodRegistry troubleshootingToolRegistry = new ToolMethodRegistry();

        Prompt troubleshootingPrompt = promptResolverService.ResolveAsync("Troubleshooting")
                                                            .GetAwaiter()
                                                            .GetResult();

        ToolDefinition[]? troubleshootingTools = JsonSerializer.Deserialize<ToolDefinition[]>(
            troubleshootingPrompt.AdditionalProperties["tools"]);
        foreach (ToolDefinition troubleshootingToolDefinition in troubleshootingTools ?? [])
        {
            Delegate troubleshootingToolImplementation = troubleshootingToolDefinition.Name switch
            {
                nameof(TroubleshootingTool.RunDiagnostics) => troubleshootingTool.RunDiagnostics,
                nameof(TroubleshootingTool.GetTroubleshootingGuide) => troubleshootingTool.GetTroubleshootingGuide,
                _ => throw new InvalidOperationException($"Tool '{troubleshootingToolDefinition.Name}' non supportato")
            };

            troubleshootingToolRegistry.AddTool(troubleshootingToolDefinition.Name, troubleshootingToolImplementation, troubleshootingToolDefinition);
        }

        return chatClient.CreateAIAgent(
            instructions: $"{troubleshootingPrompt.Content}\n{troubleshootingPrompt.Instructions}",
            name: nameof(TroubleshootingAgent),
            tools: [.. troubleshootingToolRegistry.CreateAllFunctions()]);
    }
}

public static class JsonToolFactory
{
    public static AIFunction ToAIFunction(this ToolDefinition tool, Delegate implementation)
    {
        var metadata = new
        {
            tool.Name,
            tool.Description,
            Parameters = tool.Parameters.Select(p => new
            {
                p.Name,
                p.Description,
                p.Required
            }).ToList()
        };

        return AIFunctionFactory.Create(implementation, tool.Name, tool.Description);
    }
}

public class ToolMethodRegistry
{
    private readonly Dictionary<string, Delegate> toolMethods = [];
    private readonly Dictionary<string, ToolDefinition> toolDefinitions = [];

    public ToolMethodRegistry AddTool(string toolName, Delegate toolMethod, ToolDefinition definition)
    {
        if (!toolMethods.TryAdd(toolName, toolMethod))
            throw new InvalidOperationException($"Tool '{toolName}' già registrato");

        ValidateToolDefinition(toolMethod, definition);
        toolDefinitions[toolName] = definition;

        return this;
    }

    public Delegate ResolveTool(string toolName)
        => toolMethods.TryGetValue(toolName, out Delegate? method)
            ? method
            : throw new InvalidOperationException($"Tool '{toolName}' non registrato");

    public AIFunction CreateFunction(string toolName)
    {
        Delegate implementation = ResolveTool(toolName);
        ToolDefinition definition = toolDefinitions.TryGetValue(toolName, out ToolDefinition? def)
            ? def
            : throw new InvalidOperationException($"Tool definition '{toolName}' non trovata");

        return AIFunctionFactory.Create(implementation, definition.Name, definition.Description);
    }

    public IEnumerable<AIFunction> CreateAllFunctions()
        => toolMethods.Keys.Select(CreateFunction);

    private void ValidateToolDefinition(Delegate implementation, ToolDefinition definition)
    {
        ParameterInfo[] methodParams = implementation.Method.GetParameters();
        List<ToolParameter> definitionParams = [.. definition.Parameters];

        if (methodParams.Length != definitionParams.Count)
            throw new ArgumentException($"Parameter count mismatch: method has {methodParams.Length}, definition has {definitionParams.Count}");

        for (int i = 0; i < methodParams.Length; i++)
        {
            ParameterInfo methodParam = methodParams[i];
            ToolParameter defParam = definitionParams.FirstOrDefault(p => p.Name == methodParam.Name)
                                        ?? throw new ArgumentException($"Parameter '{methodParam.Name}' non trovato nella definition");

            // Valida required vs optional
            bool isOptional = methodParam.HasDefaultValue;
            if (defParam.Required && isOptional)
                throw new ArgumentException($"Parameter '{methodParam.Name}' è required nella definition ma optional nel metodo");
        }
    }
}