using Microsoft.Extensions.AI;
using System.Reflection;
using static Morgana.AI.Records;

namespace Morgana.AI.Adapters;

public class ToolAdapter
{
    private readonly Dictionary<string, Delegate> toolMethods = [];
    private readonly Dictionary<string, ToolDefinition> toolDefinitions = [];
    private readonly List<GlobalPolicy> globalPolicies;
        
    public ToolAdapter(List<GlobalPolicy> globalPolicies)
    {
        this.globalPolicies = globalPolicies;
    }

    public ToolAdapter AddTool(string toolName, Delegate toolMethod, ToolDefinition definition)
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

        string contextGuidance = globalPolicies.FirstOrDefault(p =>
            string.Equals(p.Name, "ToolParameterContextGuidance", StringComparison.OrdinalIgnoreCase))?.Description ?? "";
        string requestGuidance = globalPolicies.FirstOrDefault(p =>
            string.Equals(p.Name, "ToolParameterRequestGuidance", StringComparison.OrdinalIgnoreCase))?.Description ?? "";

        Dictionary<string, object?> additionalProperties = [];
        foreach (ToolParameter parameter in definition.Parameters)
        {
            string parameterGuidance = parameter.Scope?.ToLowerInvariant().Trim() switch
            {
                "context" => $"{parameter.Description}. {contextGuidance}",
                "request" => $"{parameter.Description}. {requestGuidance}",
                _ => parameter.Description
            };
        
            additionalProperties[parameter.Name] = parameterGuidance;
        }

        return AIFunctionFactory.Create(implementation, new AIFunctionFactoryOptions
        {
            Name = definition.Name,
            Description = definition.Description,
            AdditionalProperties = new AdditionalPropertiesDictionary(additionalProperties)
        });
    }

    public IEnumerable<AIFunction> CreateAllFunctions()
        => toolMethods.Keys.Select(CreateFunction);

    private static void ValidateToolDefinition(Delegate implementation, ToolDefinition definition)
    {
        ParameterInfo[] methodParams = implementation.Method.GetParameters();
        List<ToolParameter> definitionParams = [.. definition.Parameters];

        if (methodParams.Length != definitionParams.Count)
            throw new ArgumentException($"Parameter count mismatch: method has {methodParams.Length}, definition has {definitionParams.Count}");

        foreach (ParameterInfo methodParam in methodParams)
        {
            ParameterInfo param = methodParam;
            ToolParameter defParam = definitionParams.FirstOrDefault(p => p.Name == param.Name)
                                     ?? throw new ArgumentException($"Parameter '{methodParam.Name}' non trovato nella definition");

            bool isOptional = methodParam.HasDefaultValue;
            if (defParam.Required && isOptional)
                throw new ArgumentException($"Parameter '{methodParam.Name}' è required nella definition ma optional nel metodo");
        }
    }
}