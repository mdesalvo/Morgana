using System.Reflection;
using Microsoft.Extensions.AI;

namespace Morgana.AI.Adapters;

/// <summary>
/// Adapter for registering and managing tool implementations for AI agents.
/// Bridges between Morgana tool definitions (from configuration) and Microsoft.Extensions.AI AIFunction system.
/// </summary>
/// <remarks>
/// Bridges between Morgana tool definitions (from agents.json) and Microsoft.Extensions.AI AIFunction system.
/// Manages registration of tool method delegates against their definitions, validates delegate signatures,
/// converts them to AIFunction instances for LLM tool calling, and applies global policies
/// (context vs request guidance) to parameter descriptions. Workflow: Create adapter → AddTool for each →
/// CreateAllFunctions to generate AIFunction[] → pass to AIAgent.
/// </remarks>
public class MorganaToolAdapter
{
    /// <summary>
    /// Placeholder in the ToolDescriptionContextGuidance injection template, resolved to the
    /// comma-separated names of the tool's own context-scoped parameters.
    /// </summary>
    private const string ContextParametersPlaceholder = "((context_parameters))";

    /// <summary>
    /// Dictionary mapping tool names to their delegate implementations.
    /// </summary>
    private readonly Dictionary<string, Delegate> toolMethods = [];

    /// <summary>
    /// Dictionary mapping tool names to their configuration definitions.
    /// </summary>
    private readonly Dictionary<string, Records.ToolDefinition> toolDefinitions = [];

    /// <summary>
    /// Global policies from Morgana configuration (e.g., context handling rules).
    /// Applied to tool parameter descriptions to guide LLM behavior.
    /// </summary>
    private readonly List<Records.GlobalPolicy> globalPolicies;

    /// <summary>
    /// Initializes a new instance of the MorganaToolAdapter with global policy enforcement.
    /// </summary>
    /// <param name="globalPolicies">Global policies from Morgana prompt configuration</param>
    public MorganaToolAdapter(List<Records.GlobalPolicy> globalPolicies)
    {
        this.globalPolicies = globalPolicies;
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
    public AIFunction CreateFunction(string toolName)
    {
        Delegate implementation = ResolveTool(toolName);
        Records.ToolDefinition definition = toolDefinitions.TryGetValue(toolName, out Records.ToolDefinition? def)
            ? def
            : throw new InvalidOperationException($"Tool definition '{toolName}' not found");

        string descriptionGuidance = Records.GlobalPolicy.ResolveTemplate(globalPolicies, Records.GlobalPolicy.Templates.ToolDescriptionContext);

        // Extract context-scoped parameter names; if present and guidance template exists, splice names into guidance text
        string[] contextParameters = [.. definition.Parameters
            .Where(p => string.Equals(p.Scope?.Trim(), "context", StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Name)];

        string description = contextParameters.Length > 0 && descriptionGuidance.Length > 0
            ? $"{definition.Description}\n\n{descriptionGuidance.Replace(ContextParametersPlaceholder, string.Join(", ", contextParameters))}"
            : definition.Description;

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
    ///     tools: toolAdapter.CreateAllFunctions().ToArray()
    /// );
    /// </code>
    /// </remarks>
    public IEnumerable<AIFunction> CreateAllFunctions()
        => toolMethods.Keys.Select(CreateFunction);

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