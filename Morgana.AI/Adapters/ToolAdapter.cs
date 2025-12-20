using Microsoft.Extensions.AI;
using System.Reflection;
using static Morgana.AI.Records;

namespace Morgana.AI.Adapters
{
    public class ToolAdapter
    {
        private readonly Dictionary<string, Delegate> toolMethods = [];
        private readonly Dictionary<string, ToolDefinition> toolDefinitions = [];
        private readonly Prompt morganaPrompt;
        
        public ToolAdapter(Prompt morganaPrompt)
        {
            this.morganaPrompt = morganaPrompt;
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

            Dictionary<string, object?> additionalProperties = [];
            foreach (ToolParameter parameter in definition.Parameters)
            {
                string parameterGuidance = parameter.Scope?.ToLowerInvariant() switch
                {
                    "context" => $"{parameter.Description}. {morganaPrompt.GetAdditionalProperty<string>("ContextToolParameterGuidance")}",
                    "request" => $"{parameter.Description}. {morganaPrompt.GetAdditionalProperty<string>("RequestToolParameterGuidance")}",
                    _ => parameter.Description // Fallback se Scope non è valorizzato o ha valore inatteso
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
}