using Microsoft.Extensions.AI;
using System.Reflection;
using static Morgana.AI.Records;

namespace Morgana.AI.Adapters;

/// <summary>
/// Adapter for registering and managing tool implementations for AI agents.
/// Bridges between Morgana tool definitions (from configuration) and Microsoft.Extensions.AI AIFunction system.
/// </summary>
/// <remarks>
/// <para><strong>Purpose:</strong></para>
/// <para>The ToolAdapter manages the registration of tool methods (delegates) and their corresponding definitions,
/// then converts them into AIFunction instances that can be used by AI agents for tool calling during LLM interactions.</para>
/// <para><strong>Key Responsibilities:</strong></para>
/// <list type="bullet">
/// <item><term>Tool Registration</term><description>Associates tool names with delegate implementations and definitions</description></item>
/// <item><term>Parameter Validation</term><description>Ensures delegate signatures match tool definitions</description></item>
/// <item><term>AIFunction Creation</term><description>Converts registered tools into AIFunction instances</description></item>
/// <item><term>Policy Application</term><description>Applies global policies to tool parameter descriptions (e.g., context vs request guidance)</description></item>
/// </list>
/// <para><strong>Workflow:</strong></para>
/// <code>
/// 1. Create ToolAdapter with global policies
/// 2. AddTool(name, delegate, definition) for each tool
/// 3. CreateAllFunctions() to generate AIFunction[]
/// 4. Pass AIFunction[] to AIAgent creation
/// 5. Agent uses tools during LLM interactions via tool calling
/// </code>
/// </remarks>
public class ToolAdapter
{
    /// <summary>
    /// Dictionary mapping tool names to their delegate implementations.
    /// </summary>
    private readonly Dictionary<string, Delegate> toolMethods = [];

    /// <summary>
    /// Dictionary mapping tool names to their configuration definitions.
    /// </summary>
    private readonly Dictionary<string, ToolDefinition> toolDefinitions = [];

    /// <summary>
    /// Global policies from Morgana configuration (e.g., context handling rules).
    /// Applied to tool parameter descriptions to guide LLM behavior.
    /// </summary>
    private readonly List<GlobalPolicy> globalPolicies;

    /// <summary>
    /// Initializes a new instance of the ToolAdapter with global policy enforcement.
    /// </summary>
    /// <param name="globalPolicies">Global policies from Morgana prompt configuration</param>
    public ToolAdapter(List<GlobalPolicy> globalPolicies)
    {
        this.globalPolicies = globalPolicies;
    }

    /// <summary>
    /// Registers a tool with its implementation delegate and configuration definition.
    /// Validates that the delegate signature matches the tool definition.
    /// </summary>
    /// <param name="toolName">Unique name of the tool (e.g., "GetInvoices", "RunDiagnostics")</param>
    /// <param name="toolMethod">Delegate implementing the tool logic</param>
    /// <param name="definition">Tool definition from configuration with parameters and metadata</param>
    /// <returns>The ToolAdapter instance for fluent chaining</returns>
    /// <exception cref="InvalidOperationException">Thrown if tool name is already registered</exception>
    /// <exception cref="ArgumentException">Thrown if delegate signature doesn't match definition</exception>
    /// <remarks>
    /// <para><strong>Validation Checks:</strong></para>
    /// <list type="bullet">
    /// <item>Tool name uniqueness (no duplicate registrations)</item>
    /// <item>Parameter count match between delegate and definition</item>
    /// <item>Parameter names match between delegate and definition</item>
    /// <item>Required parameters are not optional in the delegate</item>
    /// </list>
    /// <para><strong>Example Usage:</strong></para>
    /// <code>
    /// toolAdapter
    ///     .AddTool("GetInvoices", getInvoicesDelegate, getInvoicesDef)
    ///     .AddTool("GetInvoiceDetails", getDetailsDelegate, getDetailsDef);
    /// </code>
    /// </remarks>
    public ToolAdapter AddTool(string toolName, Delegate toolMethod, ToolDefinition definition)
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
    /// Creates an AIFunction instance for a registered tool.
    /// Applies global policies to parameter descriptions based on parameter scope (context vs request).
    /// </summary>
    /// <param name="toolName">Name of the tool to create function for</param>
    /// <returns>AIFunction instance ready for agent use</returns>
    /// <exception cref="InvalidOperationException">Thrown if tool or definition not found</exception>
    /// <remarks>
    /// <para><strong>Parameter Scope Processing:</strong></para>
    /// <list type="bullet">
    /// <item><term>Scope: "context"</term><description>Appends ToolParameterContextGuidance (check GetContextVariable first)</description></item>
    /// <item><term>Scope: "request"</term><description>Appends ToolParameterRequestGuidance (use directly from request)</description></item>
    /// <item><term>No scope</term><description>Uses parameter description as-is</description></item>
    /// </list>
    /// <para><strong>Context Guidance Example:</strong></para>
    /// <code>
    /// // Original description from agents.json
    /// "Alphanumeric identifier of the user"
    ///
    /// // After applying ToolParameterContextGuidance
    /// "Alphanumeric identifier of the user. BEFORE INVOKING THIS TOOL: call GetContextVariable
    /// to verify if the information is already available. Ask the user ONLY if GetContextVariable
    /// returns that the information is missing."
    /// </code>
    /// <para>This guidance ensures the LLM checks context before asking users for information.</para>
    /// </remarks>
    public AIFunction CreateFunction(string toolName)
    {
        Delegate implementation = ResolveTool(toolName);
        ToolDefinition definition = toolDefinitions.TryGetValue(toolName, out ToolDefinition? def)
            ? def
            : throw new InvalidOperationException($"Tool definition '{toolName}' not found");

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
    /// Validates that a delegate implementation matches its tool definition.
    /// Ensures parameter counts, names, and optionality are consistent.
    /// 
    /// PATCHED FOR MCP: Skips hidden Closure parameters that .NET adds when compiling
    /// Expression Trees that capture variables. This allows MCP-generated delegates
    /// to work correctly.
    /// </summary>
    /// <param name="implementation">Delegate to validate</param>
    /// <param name="definition">Tool definition to validate against</param>
    /// <exception cref="ArgumentException">Thrown on validation failure</exception>
    /// <remarks>
    /// <para><strong>Validation Rules:</strong></para>
    /// <list type="number">
    /// <item>Parameter count must match exactly (excluding Closure parameters)</item>
    /// <item>Each definition parameter must have a matching method parameter by name</item>
    /// <item>Required parameters in definition cannot be optional in method</item>
    /// </list>
    /// <para><strong>MCP Closure Handling:</strong></para>
    /// <para>When MCP tools are converted to delegates, .NET may add hidden Closure parameters
    /// to capture context. These parameters have no name and type "Closure". We skip them
    /// during validation since they're implementation details, not actual tool parameters.</para>
    /// </remarks>
    /// <summary>
    /// Validates that a delegate implementation matches its tool definition.
    /// Ensures parameter counts, names, and optionality are consistent.
    /// 
    /// PATCHED FOR MCP: Skips hidden Closure parameters that .NET may add.
    /// This provides backward compatibility and defense in depth.
    /// </summary>
    private static void ValidateToolDefinition(Delegate implementation, ToolDefinition definition)
    {
        ParameterInfo[] allParams = implementation.Method.GetParameters();
        List<ToolParameter> definitionParams = [.. definition.Parameters];

        // MCP: Skip the first parameter if it's a Closure parameter
        ParameterInfo[] methodParams = allParams;
        if (allParams.Length > 0 && IsClosureParameter(allParams[0]))
        {
            // Skip the first Closure parameter
            methodParams = allParams.Skip(1).ToArray();
        }

        if (methodParams.Length != definitionParams.Count)
            throw new ArgumentException(
                $"Parameter count mismatch: method has {methodParams.Length} (filtered from {allParams.Length} total), definition has {definitionParams.Count}");

        // Validate parameters by name
        foreach (ParameterInfo methodParam in methodParams)
        {
            string paramName = methodParam.Name ?? string.Empty;
            
            // With DynamicMethod + DefineParameter, all parameters should have names
            // But we keep this flexible for backward compatibility
            ToolParameter defParam = definitionParams.FirstOrDefault(p => p.Name == paramName)
                ?? throw new ArgumentException($"Parameter '{paramName}' not found in definition");

            bool isOptional = methodParam.HasDefaultValue;
            if (defParam.Required && isOptional)
                throw new ArgumentException($"Parameter '{defParam.Name}' is required in definition but optional in method");
        }
    }

    /// <summary>
    /// Determines if a parameter is a Closure parameter added by .NET's Expression Tree compiler.
    /// Closure parameters are hidden implementation details used to capture variables in lambdas.
    /// </summary>
    /// <param name="param">Parameter to check</param>
    /// <returns>True if this is a Closure parameter, false otherwise</returns>
    private static bool IsClosureParameter(ParameterInfo param)
    {
        // Closure parameters typically have:
        // 1. Empty or null name
        // 2. Type name containing "Closure" or compiler-generated markers
        string paramName = param.Name ?? string.Empty;
        string typeName = param.ParameterType.Name;
        
        // Check for empty name (common for Closure params)
        if (string.IsNullOrWhiteSpace(paramName))
            return true;
        
        // Check for Closure type name
        if (typeName.Contains("Closure", StringComparison.OrdinalIgnoreCase))
            return true;
        
        // Check for compiler-generated type markers
        if (typeName.Contains("<>") || typeName.Contains("DisplayClass"))
            return true;
        
        return false;
    }
}