using System.ComponentModel;
using System.Reflection;
using Microsoft.Extensions.AI;
using Morgana.AI.Attributes;
using Morgana.AI.Interfaces;

namespace Morgana.AI.Adapters;

/// <summary>Bridges the tool classes whose methods carry their own descriptions to the AIFunctions the model calls.</summary>
public class MorganaToolAdapter
{
    /// <summary>The bound method of each registered tool, by tool name.</summary>
    private readonly Dictionary<string, Delegate> toolMethods = [];

    /// <summary>The definition each registered tool was projected into, by tool name.</summary>
    private readonly Dictionary<string, Records.ToolDefinition> toolDefinitions = [];

    /// <summary>
    /// Composes the description each tool presents to the model.
    /// It splices the framework's context guidance into the tools that declare context-scoped parameters.
    /// </summary>
    private readonly IPromptComposerService promptComposerService;

    /// <summary>Binds the adapter to the composer of the tools' descriptions.</summary>
    /// <param name="promptComposerService">Composes the descriptions the model reads.</param>
    public MorganaToolAdapter(IPromptComposerService promptComposerService)
    {
        this.promptComposerService = promptComposerService;
    }

    /// <summary>Lists the tools a tool class declares: the public instance methods it writes or overrides, in declaration order.</summary>
    /// <param name="toolType">The tool class to read.</param>
    /// <returns>The methods that are tools, in the order the model reads them.</returns>
    public static IReadOnlyList<MethodInfo> GetToolMethods(Type toolType)
    {
        // The methods declared on the class itself: whatever a base class offers is infrastructure and no tool.
        MethodInfo[] declaredMethods = toolType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

        // The metadata token follows source order, which reflection alone does not promise
        // and the model reads the tools in that order.
        return [.. declaredMethods
            .Where(method => !method.IsSpecialName && IsTool(method))
            .OrderBy(method => method.MetadataToken)];
    }

    /// <summary>Tells a tool from the other public methods a tool class declares.</summary>
    private static bool IsTool(MethodInfo method)
    {
        // The definition the method overrides or the method itself where it overrides nothing.
        MethodInfo baseDefinition = method.GetBaseDefinition();

        // A method the class introduces is a tool, unless it is virtual: then it is an implementation
        // the subclasses take up as a tool by overriding it, each with its own description.
        if (baseDefinition.DeclaringType == method.DeclaringType)
            return !method.IsVirtual || method.IsFinal;

        // An override is the class's own tool, except one of object's members such as ToString.
        return baseDefinition.DeclaringType != typeof(object);
    }

    /// <summary>Projects the tool definitions a tool class declares through its methods and their attributes.</summary>
    /// <param name="toolType">The tool class to read.</param>
    /// <returns>One definition per tool method, in declaration order.</returns>
    public static IReadOnlyList<Records.ToolDefinition> ProjectToolDefinitions(Type toolType)
    {
        // Never throws: a missing description or an incoherent parameter projects as it is
        // because refusing a malformed class is the startup check's job.
        List<Records.ToolDefinition> toolDefinitions = [];

        // One definition per tool method, in the order the model reads them.
        foreach (MethodInfo toolMethod in GetToolMethods(toolType))
        {
            // The parameters in signature order, each described by its own attributes.
            List<Records.ToolParameter> toolParameters = [];
            foreach (ParameterInfo parameter in toolMethod.GetParameters())
            {
                // A parameter without [ToolParameter] holds a value the model itself authors: it has no scope.
                ToolParameterAttribute? parameterDeclaration = parameter.GetCustomAttribute<ToolParameterAttribute>();
                string? parameterScope = parameterDeclaration?.Scope switch
                {
                    // This agent's context or the conversation's shared one holds the value.
                    Records.ToolScope.Context or Records.ToolScope.SharedContext => Constants.Scopes.Context,

                    // The user is asked for the value.
                    Records.ToolScope.Request => Constants.Scopes.Request,

                    // No declaration: the model authors the value.
                    _ => null
                };

                // A parameter with a default value is optional for the model; only a shared context value reaches the other agents.
                toolParameters.Add(new Records.ToolParameter(
                    parameter.Name ?? string.Empty,
                    parameter.GetCustomAttribute<DescriptionAttribute>()?.Description ?? string.Empty,
                    Required: !parameter.HasDefaultValue,
                    Scope: parameterScope,
                    Shared: parameterDeclaration?.Scope == Records.ToolScope.SharedContext));
            }

            // The tool as the model reads it: its name and description with the parameters just projected.
            toolDefinitions.Add(new Records.ToolDefinition(
                toolMethod.Name,
                toolMethod.GetCustomAttribute<DescriptionAttribute>()?.Description ?? string.Empty,
                toolParameters));
        }

        // The projection of the whole class.
        return toolDefinitions;
    }

    /// <summary>Registers one tool under its name, refusing a name already taken or a definition that does not match the method.</summary>
    private void AddTool(string toolName, Delegate toolMethod, Records.ToolDefinition definition)
    {
        // Two tools under one name would have the provider refuse the agent's whole tool list.
        if (!toolMethods.TryAdd(toolName, toolMethod))
            throw new InvalidOperationException($"Tool '{toolName}' already registered");

        // The definition must describe this very method: otherwise the model would be offered parameters it cannot fill.
        ValidateToolDefinition(toolMethod, definition);
        toolDefinitions[toolName] = definition;
    }

    /// <summary>Binds each of the given definitions to the method of the tool instance that carries its name.</summary>
    /// <param name="toolInstance">The tool instance whose methods implement the tools.</param>
    /// <param name="toolDefinitions">The definitions naming the methods to register.</param>
    /// <returns>This adapter, so a caller can bind several instances in one expression.</returns>
    public MorganaToolAdapter AddTools(object toolInstance, IEnumerable<Records.ToolDefinition> toolDefinitions)
    {
        // Each definition names one method of the instance and becomes one registered tool.
        foreach (Records.ToolDefinition definition in toolDefinitions)
        {
            // The definition was projected from this very method, so the method always exists.
            MethodInfo method = toolInstance.GetType().GetMethod(definition.Name)!;

            // Tools live on plugin classes discovered at runtime, so the delegate's exact Func<...> type
            // is computed from the method's own signature and bound to the instance.
            Delegate implementation = Delegate.CreateDelegate(
                System.Linq.Expressions.Expression.GetDelegateType(
                [
                    .. method.GetParameters().Select(parameter => parameter.ParameterType),
                    method.ReturnType
                ]),
                toolInstance,
                method);

            // Registered under the tool's name, which a second registration of the same name is refused on.
            AddTool(definition.Name, implementation, definition);
        }

        // Chained so a caller can bind several instances in one expression.
        return this;
    }

    /// <summary>Turns one registered tool into the function the model calls, with its composed description and its parameters described in the schema.</summary>
    private async Task<AIFunction> CreateFunctionAsync(string toolName)
    {
        // Both lookups are of a name AddTool registered, so neither can miss.
        Delegate implementation = toolMethods[toolName];
        Records.ToolDefinition definition = toolDefinitions[toolName];

        // The tool's description with the framework's context guidance spliced in where its parameters call for it.
        string description = await promptComposerService.ComposeToolDescriptionAsync(definition);

        // Each parameter's description, which reaches the model only through the generated JSON schema.
        Dictionary<string, string> parameterDescriptions =
            definition.Parameters.ToDictionary(parameter => parameter.Name, parameter => parameter.Description);

        // The function under the tool's name, its schema describing each parameter from the projected definition.
        return AIFunctionFactory.Create(implementation,
            new AIFunctionFactoryOptions
            {
                Name = definition.Name,
                Description = description,
                JsonSchemaCreateOptions = AIJsonSchemaCreateOptions.Default with
                {
                    ParameterDescriptionProvider = parameter =>
                        parameter.Name is not null
                        && parameterDescriptions.TryGetValue(parameter.Name, out string? parameterDescription)
                            ? parameterDescription
                            : null
                }
            });
    }

    /// <summary>Turns every registered tool into the function the model calls, in registration order.</summary>
    public async Task<AIFunction[]> CreateAllFunctionsAsync()
        => await Task.WhenAll(toolMethods.Keys.Select(CreateFunctionAsync));

    /// <summary>Refuses a definition whose parameters differ from the method's in number or name and one that requires a parameter the method makes optional.</summary>
    private static void ValidateToolDefinition(Delegate implementation, Records.ToolDefinition definition)
    {
        // The method is the truth the definition must agree with, parameter by parameter.
        ParameterInfo[] methodParameters = implementation.Method.GetParameters();
        List<Records.ToolParameter> definitionParameters = [.. definition.Parameters];

        // A parameter missing from either side is a definition projected from another method.
        if (methodParameters.Length != definitionParameters.Count)
            throw new ArgumentException($"Parameter count mismatch: method has {methodParameters.Length}, definition has {definitionParameters.Count}");

        // Each of the method's parameters must be declared under its own name.
        foreach (ParameterInfo methodParameter in methodParameters)
        {
            Records.ToolParameter definitionParameter = definitionParameters.FirstOrDefault(parameter => parameter.Name == methodParameter.Name)
                ?? throw new ArgumentException($"Parameter '{methodParameter.Name}' not found in definition");

            // The model would be made to fill a parameter the method can do without.
            if (definitionParameter.Required && methodParameter.HasDefaultValue)
                throw new ArgumentException($"Parameter '{methodParameter.Name}' is required in definition but optional in method");
        }
    }
}