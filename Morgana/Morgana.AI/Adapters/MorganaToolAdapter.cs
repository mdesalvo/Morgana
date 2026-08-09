using System.Reflection;
using Microsoft.Extensions.AI;
using Morgana.AI.Interfaces;

namespace Morgana.AI.Adapters;

/// <summary>
/// Adapter for registering and managing tool implementations for AI agents.
/// Bridges between Morgana tool definitions (from configuration) and Microsoft.Extensions.AI AIFunction system.
/// </summary>
/// <remarks>
/// Bridges between Morgana tool definitions (from agents.json) and Microsoft.Extensions.AI AIFunction system.
/// Manages registration of tool method delegates against their definitions, validates delegate signatures,
/// and converts them to AIFunction instances for LLM tool calling. Tool descriptions are assembled by
/// <see cref="IPromptComposerService"/>; parameter descriptions are passed through as authored.
/// Workflow: Create adapter → AddTool for each → CreateAllFunctions to generate AIFunction[] → pass to AIAgent.
/// </remarks>
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
    /// Checks parameter count, names, and required-vs-optional consistency.
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