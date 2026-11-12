using System.ComponentModel;
using System.Reflection;
using Microsoft.Extensions.AI;
using Morgana.AI.Attributes;
using Morgana.AI.Interfaces;

namespace Morgana.AI.Adapters;

/// <summary>Bridges the tool classes whose methods carry their own descriptions to the AIFunctions the model calls.</summary>
public class MorganaToolAdapter
{
    /// <summary>
    /// Dictionary mapping tool names to their delegate implementations.
    /// </summary>
    private readonly Dictionary<string, Delegate> toolMethods = [];

    /// <summary>
    /// Dictionary mapping tool names to their configuration definitions.
    /// </summary>
    private readonly Dictionary<string, Records.ToolDefinition> toolDefinitions = [];

    /// <summary>
    /// Assembles the description each generated AIFunction presents to the model, splicing the
    /// framework's context guidance into the tools that declare context-scoped parameters.
    /// </summary>
    private readonly IPromptComposerService promptComposerService;

    /// <summary>
    /// Initializes a new instance of the MorganaToolAdapter.
    /// </summary>
    /// <param name="promptComposerService">Composes the descriptions exposed to the model</param>
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

    /// <summary>
    /// Registers a tool implementation with validation and fluent chaining support.
    /// Validates delegate signature matches tool definition (parameter count, names, required flags).
    /// </summary>
    /// <param name="toolName">Unique tool name</param>
    /// <param name="toolMethod">Delegate implementing the tool</param>
    /// <param name="definition">Tool definition with parameters and metadata</param>
    /// <returns>This adapter for method chaining</returns>
    public MorganaToolAdapter AddTool(string toolName, Delegate toolMethod, Records.ToolDefinition definition)
    {
        if (!toolMethods.TryAdd(toolName, toolMethod))
            throw new InvalidOperationException($"Tool '{toolName}' already registered");

        ValidateToolDefinition(toolMethod, definition);
        toolDefinitions[toolName] = definition;

        return this;
    }

    /// <summary>Binds each of the given definitions to the method of the tool instance that carries its name.</summary>
    /// <param name="toolInstance">The tool instance containing the tool method implementations.</param>
    /// <param name="toolDefinitions">The definitions specifying which methods to register from the instance.</param>
    /// <returns>This adapter for method chaining</returns>
    public MorganaToolAdapter AddTools(object toolInstance, IEnumerable<Records.ToolDefinition> toolDefinitions)
    {
        // Each definition names one method of the instance and becomes one registered tool.
        foreach (Records.ToolDefinition definition in toolDefinitions)
        {
            // The definition was projected from this very method, so the method always exists.
            MethodInfo method = toolInstance.GetType().GetMethod(definition.Name)!;

            // Build a strongly-typed delegate whose exact Func<…> type is computed from
            // the method's own ParameterInfo at runtime: tools are discovered on
            // plugin classes and bound to implementations via reflection, so the
            // concrete delegate type is unknowable at compile time.
            Delegate implementation = Delegate.CreateDelegate(
                System.Linq.Expressions.Expression.GetDelegateType(
                [
                    .. method.GetParameters().Select(p => p.ParameterType),
                    method.ReturnType
                ]),
                toolInstance,
                method);

            AddTool(definition.Name, implementation, definition);
        }

        // Chained so a caller can bind several instances in one expression.
        return this;
    }

    /// <summary>
    /// Resolves a tool delegate by name.
    /// </summary>
    /// <param name="toolName">Name of the tool to resolve</param>
    /// <returns>Delegate implementation for the tool</returns>
    /// <exception cref="InvalidOperationException">Thrown if tool is not registered</exception>
    public Delegate ResolveTool(string toolName)
        => toolMethods.TryGetValue(toolName, out Delegate? method)
            ? method
            : throw new InvalidOperationException($"Tool '{toolName}' not registered");

    /// <summary>
    /// Creates an AIFunction instance for a registered tool with resolved descriptions.
    /// Applies global policies and resolves context-parameter placeholders in tool descriptions.
    /// Converts the tool delegate into a JSON schema-ready AIFunction for the LLM.
    /// </summary>
    /// <param name="toolName">Name of the tool to create function for</param>
    /// <returns>AIFunction instance ready for agent use</returns>
    /// <exception cref="InvalidOperationException">Thrown if tool or definition not found</exception>
    public async Task<AIFunction> CreateFunctionAsync(string toolName)
    {
        Delegate implementation = ResolveTool(toolName);
        Records.ToolDefinition definition = toolDefinitions.TryGetValue(toolName, out Records.ToolDefinition? def)
            ? def
            : throw new InvalidOperationException($"Tool definition '{toolName}' not found");

        string description = await promptComposerService.ComposeToolDescriptionAsync(definition);

        // Build parameter name → description map; fed to AIFunctionFactory's ParameterDescriptionProvider hook,
        // which resolves each parameter's description keyword in the generated JSON schema
        Dictionary<string, string> parameterDescriptions =
            definition.Parameters.ToDictionary(p => p.Name, p => p.Description);

        // Create AIFunction with custom ParameterDescriptionProvider that looks up each parameter's description
        // from the map; unknown parameters fall back to null, which lets AIFunctionFactory use [Description] attributes (none here)
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

    /// <summary>
    /// Creates AIFunction instances for all registered tools.
    /// </summary>
    /// <returns>Enumerable of AIFunction instances ready for agent use</returns>
    /// <remarks>
    /// <para>This is typically called during agent creation to pass all tools to the AIAgent constructor.</para>
    /// <code>
    /// AIAgent agent = chatClient.CreateAIAgent(
    ///     instructions: instructions,
    ///     name: "billing",
    ///     tools: await toolAdapter.CreateAllFunctionsAsync()
    /// );
    /// </code>
    /// </remarks>
    public async Task<AIFunction[]> CreateAllFunctionsAsync()
        => await Task.WhenAll(toolMethods.Keys.Select(CreateFunctionAsync));

    /// <summary>
    /// Validates delegate implementation matches tool definition.
    /// Checks parameter count, names and required-vs-optional consistency.
    /// </summary>
    /// <param name="implementation">Delegate to validate</param>
    /// <param name="definition">Tool definition to validate against</param>
    private static void ValidateToolDefinition(Delegate implementation, Records.ToolDefinition definition)
    {
        ParameterInfo[] methodParams = implementation.Method.GetParameters();
        List<Records.ToolParameter> definitionParams = [.. definition.Parameters];

        if (methodParams.Length != definitionParams.Count)
            throw new ArgumentException($"Parameter count mismatch: method has {methodParams.Length}, definition has {definitionParams.Count}");

        foreach (ParameterInfo methodParam in methodParams)
        {
            ParameterInfo param = methodParam;
            Records.ToolParameter defParam = definitionParams.FirstOrDefault(p => p.Name == param.Name)
                                             ?? throw new ArgumentException($"Parameter '{methodParam.Name}' not found in definition");

            bool isOptional = methodParam.HasDefaultValue;
            if (defParam.Required && isOptional)
                throw new ArgumentException($"Parameter '{methodParam.Name}' is required in definition but optional in method");
        }
    }
}