using System.Reflection;
using Microsoft.Extensions.AI;

namespace Morgana.AI.Adapters;

/// <summary>
/// Adapter for registering and managing tool implementations for AI agents.
/// Bridges between Morgana tool definitions (from configuration) and Microsoft.Extensions.AI AIFunction system.
/// </summary>
/// <remarks>
/// <para><strong>Purpose:</strong></para>
/// <para>The MorganaToolAdapter manages the registration of tool methods (delegates) and their corresponding definitions,
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
/// 1. Create MorganaToolAdapter with global policies
/// 2. AddTool(name, delegate, definition) for each tool
/// 3. CreateAllFunctions() to generate AIFunction[]
/// 4. Pass AIFunction[] to AIAgent creation
/// 5. Agent uses tools during LLM interactions via tool calling
/// </code>
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
    /// Registers a tool with its implementation delegate and configuration definition.
    /// Validates that the delegate signature matches the tool definition.
    /// </summary>
    /// <param name="toolName">Unique name of the tool (e.g., "GetInvoices", "RunDiagnostics")</param>
    /// <param name="toolMethod">Delegate implementing the tool logic</param>
    /// <param name="definition">Tool definition from configuration with parameters and metadata</param>
    /// <returns>The MorganaToolAdapter instance for fluent chaining</returns>
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
    /// Creates an AIFunction instance for a registered tool.
    /// Applies global policies to parameter descriptions based on parameter scope (context vs request).
    /// </summary>
    /// <param name="toolName">Name of the tool to create function for</param>
    /// <returns>AIFunction instance ready for agent use</returns>
    /// <exception cref="InvalidOperationException">Thrown if tool or definition not found</exception>
    /// <remarks>
    /// <para><strong>Tool Description Processing:</strong></para>
    /// <para>
    /// A tool declaring at least one context-scoped parameter gets ToolDescriptionContextGuidance
    /// appended to its own description, with <c>((context_parameters))</c> resolved to the names of
    /// those parameters. This placement is deliberate: a parameter description is read once the
    /// model is already invoking the tool, whereas the failure it guards against — asking the user
    /// for a value the conversation store already holds — happens one step earlier, when the model
    /// weighs the tool at all. The tool description is what it reads then.
    /// </para>
    /// <para><strong>Parameter Scope Processing.</strong> The assembled descriptions are handed to
    /// <c>AIJsonSchemaCreateOptions.ParameterDescriptionProvider</c>, which emits each one as the
    /// <c>description</c> keyword of its parameter in the function's JSON schema — the only place the
    /// model reads a parameter description at all.</para>
    /// <para>No scope adds anything to a parameter description any more — every parameter is emitted
    /// with its authored prose and nothing else. The context scope lost its template at A2.5.2 and the
    /// request scope at A2.5.5; both restated, per-parameter and on every round trip, a rule already
    /// carried by P0, by the framework Instructions, by the tool-level template spliced below, and by
    /// the per-turn HeldContextDeclaration. Scope still decides which parameter names are named in the
    /// <em>tool's</em> description — see below.</para>
    /// <para>A template left unconfigured leaves the parameter description untouched rather than
    /// appending an empty one, which would trail a stray space on every such parameter.</para>
    /// <para><strong>Why the context scope has no parameter-level template.</strong> It used to: a
    /// fragment reading "BEFORE INVOKING THIS TOOL: call GetContextVariable … Ask the user ONLY if it
    /// returns missing … ONCE YOU HOLD THE VALUE call SetContextVariable". Every clause of it, the
    /// write half included, is already carried by policy P0, by the framework Instructions and by
    /// ToolDescriptionContextGuidance above — and it was the only one of them paid per-parameter,
    /// per-tool, on every round trip. It was removed once
    /// <c>MorganaAIContextProvider.ProvideAIContextAsync</c> began stating per turn which variables
    /// the session actually holds, which is the one thing none of those restatements carried.</para>
    /// </remarks>
    public AIFunction CreateFunction(string toolName)
    {
        Delegate implementation = ResolveTool(toolName);
        Records.ToolDefinition definition = toolDefinitions.TryGetValue(toolName, out Records.ToolDefinition? def)
            ? def
            : throw new InvalidOperationException($"Tool definition '{toolName}' not found");

        string descriptionGuidance = Records.GlobalPolicy.ResolveTemplate(globalPolicies, Records.GlobalPolicy.Templates.ToolDescriptionContext);

        // Name the tool's own context-scoped parameters in its description. A tool with none
        // (every presentation tool, every purely request-scoped domain tool) is left untouched:
        // the guidance is about resolving inputs, and a tool without such inputs would only be
        // told to look up something it never takes.
        string[] contextParameters = [.. definition.Parameters
            .Where(p => string.Equals(p.Scope?.Trim(), "context", StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Name)];

        string description = contextParameters.Length > 0 && descriptionGuidance.Length > 0
            ? $"{definition.Description}\n\n{descriptionGuidance.Replace(ContextParametersPlaceholder, string.Join(", ", contextParameters))}"
            : definition.Description;

        // No scope carries a parameter-level template any more. The context scope lost its one at
        // A2.5.2 and the request scope at A2.5.5: a parameter description is read once the model is
        // already invoking the tool, which is the wrong rung for a rule, and the rules themselves
        // are carried above — by P0, by the tool-level template spliced just above, and by the
        // per-turn HeldContextDeclaration. What reaches the model here is the authored prose and
        // nothing else.
        Dictionary<string, string> parameterDescriptions =
            definition.Parameters.ToDictionary(p => p.Name, p => p.Description);

        // ParameterDescriptionProvider is the one hook that puts these descriptions where the model
        // reads them: AIFunctionFactory calls it once per parameter while generating the schema, and
        // what it returns becomes that parameter's "description" keyword. Returning null falls back to
        // the method's own metadata ([Description] attributes), which no MorganaTool method carries —
        // so a parameter absent from the map is simply emitted bare, as every parameter used to be.
        // The route this replaces, AIFunctionFactoryOptions.AdditionalProperties, is metadata hanging
        // off the AIFunction object and never entered the schema at all.
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
    /// Validates that a delegate implementation matches its tool definition.
    /// Ensures parameter counts, names, and optionality are consistent.
    /// </summary>
    /// <param name="implementation">Delegate to validate</param>
    /// <param name="definition">Tool definition to validate against</param>
    /// <exception cref="ArgumentException">Thrown on validation failure</exception>
    /// <remarks>
    /// <para><strong>Validation Rules:</strong></para>
    /// <list type="number">
    /// <item>Parameter count must match exactly</item>
    /// <item>Each definition parameter must have a matching method parameter by name</item>
    /// <item>Required parameters in definition cannot be optional in method</item>
    /// </list>
    /// <para><strong>Example Mismatch:</strong></para>
    /// <code>
    /// // Definition says: userId (Required: true)
    /// public Task&lt;Result&gt; GetInvoices(string userId = "default")
    /// // ERROR: Parameter 'userId' is required in definition but optional in method
    /// </code>
    /// </remarks>
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