using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Morgana.AI.Abstractions;
using Morgana.AI.Interfaces;

namespace Morgana.AI.Adapters;

/// <summary>
/// Adapter for registering and managing tool implementations for AI agents.
/// Bridges between Morgana tool definitions (from configuration) and Microsoft.Extensions.AI AIFunction system.
/// </summary>
/// <remarks>
/// Bridges between Morgana tool definitions (from agents.json) and Microsoft.Extensions.AI AIFunction system.
/// Manages registration of tool method delegates against their definitions, validates delegate signatures
/// and converts them to AIFunction instances for LLM tool calling. A tool declaring context-scoped
/// parameters is wrapped so the framework resolves them from the session: the model never looks a
/// value up or stores it, it calls the tool and asks the user only for what the tool reports missing.
/// Workflow: Create adapter → AddTool for each → CreateAllFunctions to generate AIFunction[] → pass to AIAgent.
/// </remarks>
public class MorganaToolAdapter
{
    /// <summary>
    /// How tool arguments are read and their schemas written. A structured argument, such as the card
    /// Reply carries, is a contract: its schema declares what is required and an argument breaking it
    /// is refused rather than half-read. The card's component type may sit anywhere in its object.
    /// </summary>
    private static readonly JsonSerializerOptions ToolSerializerOptions =
        new JsonSerializerOptions(AIJsonUtilities.DefaultOptions)
        {
            AllowOutOfOrderMetadataProperties = true,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true
        };

    /// <summary>
    /// How a tool's record is written for the model: compact, since every space of it is paid for on each
    /// turn that carries it and a provider sends a text result exactly as it receives it.
    /// </summary>
    private static readonly JsonSerializerOptions ToolResultSerializerOptions =
        new JsonSerializerOptions(ToolSerializerOptions) { WriteIndented = false };

    /// <summary>
    /// Derives the JSON schema of what a tool method returns, exactly as its <see cref="AIFunction"/> publishes it.
    /// </summary>
    /// <param name="returnType">The method's return type, already unwrapped from <c>Task</c> or <c>ValueTask</c>.</param>
    public static JsonElement CreateReturnSchema(Type returnType)
        => AIJsonUtilities.CreateJsonSchema(returnType, serializerOptions: ToolSerializerOptions, inferenceOptions: AIJsonSchemaCreateOptions.Default);

    /// <summary>
    /// Dictionary mapping tool names to their delegate implementations.
    /// </summary>
    private readonly Dictionary<string, Delegate> toolMethods = [];

    /// <summary>
    /// Dictionary mapping tool names to their configuration definitions.
    /// </summary>
    private readonly Dictionary<string, Records.ToolDefinition> toolDefinitions = [];

    /// <summary>
    /// Supplies the session that the context-scoped parameters are resolved from and stored into.
    /// Null for an adapter whose tools declare none, such as a workbench agent holding no conversation.
    /// </summary>
    private readonly Func<MorganaTool.ToolContext>? toolContextFactory;

    /// <summary>
    /// Emits the context-access lines, which the PromptHarness parses to observe the resolution.
    /// </summary>
    private readonly ILogger? logger;

    /// <summary>
    /// Composes the description that each tool presents. Null presents every tool exactly as authored.
    /// </summary>
    private readonly IPromptComposerService? promptComposerService;

    /// <summary>
    /// Initializes an adapter for tools declaring no context-scoped parameter.
    /// </summary>
    public MorganaToolAdapter() { }

    /// <summary>
    /// Initializes an adapter whose context-scoped parameters are resolved from the agent's session.
    /// </summary>
    /// <param name="logger">Logger receiving the observable context-access lines</param>
    /// <param name="toolContextFactory">Supplies the in-flight session at each tool invocation</param>
    /// <param name="promptComposerService">Composes the tool descriptions; null presents them as authored</param>
    public MorganaToolAdapter(ILogger logger, Func<MorganaTool.ToolContext> toolContextFactory, IPromptComposerService? promptComposerService = null)
    {
        this.logger = logger;
        this.toolContextFactory = toolContextFactory;
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
    /// Creates the AIFunction the model calls for a registered tool: its authored description, its
    /// parameter descriptions in the JSON schema, the session resolution of its context-scoped
    /// parameters and, where it requires execution approval, the user's approval before it runs.
    /// </summary>
    /// <param name="toolName">Name of the tool to create function for</param>
    /// <returns>AIFunction instance ready for agent use</returns>
    /// <exception cref="InvalidOperationException">Thrown if tool or definition not found or if a context-scoped tool reaches an adapter holding no session</exception>
    public async Task<AIFunction> CreateFunctionAsync(string toolName)
    {
        Delegate implementation = ResolveTool(toolName);
        Records.ToolDefinition definition = toolDefinitions.TryGetValue(toolName, out Records.ToolDefinition? def)
            ? def
            : throw new InvalidOperationException($"Tool definition '{toolName}' not found");

        // Build parameter name → description map; fed to AIFunctionFactory's ParameterDescriptionProvider hook,
        // which resolves each parameter's description keyword in the generated JSON schema
        Dictionary<string, string> parameterDescriptions =
            definition.Parameters.ToDictionary(p => p.Name, p => p.Description);

        // Create AIFunction with custom ParameterDescriptionProvider that looks up each parameter's description
        // from the map; unknown parameters fall back to null, which lets AIFunctionFactory use [Description] attributes (none here)
        AIFunction function = AIFunctionFactory.Create(implementation,
            new AIFunctionFactoryOptions
            {
                Name = definition.Name,
                Description = promptComposerService is null
                    ? definition.Description
                    : await promptComposerService.ComposeToolDescriptionAsync(definition),
                SerializerOptions = ToolSerializerOptions,

                // A framework tool's named result reaches the tool loop as itself, to be given its authored
                // text there. A domain tool's record reaches the model as compact JSON text, never as a
                // structure that a provider might write out indented.
                MarshalResult = (result, resultType, _) => new ValueTask<object?>(result switch
                {
                    null => null,
                    Records.FrameworkToolResult named => named,
                    string text => text,
                    _ => JsonSerializer.Serialize(result, resultType ?? result.GetType(), ToolResultSerializerOptions)
                }),
                JsonSchemaCreateOptions = AIJsonSchemaCreateOptions.Default with
                {
                    ParameterDescriptionProvider = parameter =>
                        parameter.Name is not null
                        && parameterDescriptions.TryGetValue(parameter.Name, out string? parameterDescription)
                            ? parameterDescription
                            : null
                }
            });

        string[] contextParameters = [.. definition.Parameters
            .Where(p => string.Equals(p.Scope?.Trim(), Constants.Scopes.Context, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Name)];

        // A context-scoped tool on an adapter with no session has nowhere to resolve its inputs from:
        // a wiring fault, refused at agent creation rather than at the first call.
        if (contextParameters.Length > 0 && (toolContextFactory is null || logger is null))
            throw new InvalidOperationException(
                $"Tool '{toolName}' declares context-scoped parameters but its adapter holds no session to resolve them from");

        // A tool resolving nothing from the session reaches the model exactly as declared.
        AIFunction resolvedFunction = contextParameters.Length == 0
            ? function
            : new ContextResolvingFunction(function, contextParameters, toolContextFactory!, logger!);

        // A tool that changes something real waits for the user's approval of the exact call before it
        // runs. Outermost, so nothing of the call is resolved or stored until the user has approved it.
        return definition.RequiresExecutionApproval
            ? new ApprovalRequiredAIFunction(resolvedFunction)
            : resolvedFunction;
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

    /// <summary>
    /// A tool whose context-scoped parameters the framework resolves: a value the model passes is
    /// stored and used, one it omits is read from the session and one the session lacks keeps the
    /// tool from running at all.
    /// </summary>
    /// <remarks>
    /// The parameters stay in the schema, so the model may still pass a value the user has just
    /// given, but they are never required of it: on every other turn the session answers for them.
    /// </remarks>
    private sealed class ContextResolvingFunction : DelegatingAIFunction
    {
        /// <summary>The tool's own context-scoped parameter names.</summary>
        private readonly string[] contextParameters;

        /// <summary>Supplies the in-flight session at each invocation.</summary>
        private readonly Func<MorganaTool.ToolContext> toolContextFactory;

        /// <summary>Emits the observable HIT, MISS and SET lines.</summary>
        private readonly ILogger logger;

        /// <summary>The tool's schema, in which the context-scoped parameters are no longer required.</summary>
        private readonly JsonElement jsonSchema;

        /// <summary>
        /// Wraps a tool generated from its delegate.
        /// </summary>
        /// <param name="innerFunction">The tool as generated from its delegate</param>
        /// <param name="contextParameters">The parameters that are resolved from the session</param>
        /// <param name="toolContextFactory">Supplies the in-flight session at each invocation</param>
        /// <param name="logger">Receives the observable context-access lines</param>
        public ContextResolvingFunction(
            AIFunction innerFunction,
            string[] contextParameters,
            Func<MorganaTool.ToolContext> toolContextFactory,
            ILogger logger) : base(innerFunction)
        {
            this.contextParameters = contextParameters;
            this.toolContextFactory = toolContextFactory;
            this.logger = logger;

            // The C# method still requires every one of them: the framework fills them in before
            // the method is reached, so only the model is released from supplying them.
            JsonObject schema = JsonNode.Parse(innerFunction.JsonSchema.GetRawText())!.AsObject();
            if (schema["required"] is JsonArray required)
                schema["required"] = new JsonArray([.. required
                    .Where(name => !contextParameters.Contains(name!.GetValue<string>(), StringComparer.Ordinal))
                    .Select(name => name!.DeepClone())]);
            jsonSchema = JsonSerializer.SerializeToElement(schema);
        }

        /// <inheritdoc />
        public override JsonElement JsonSchema => jsonSchema;

        /// <summary>
        /// Resolves every context-scoped parameter, then runs the tool only when all of them hold a value.
        /// </summary>
        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            MorganaTool.ToolContext toolContext = toolContextFactory();
            List<string> missingParameters = [];

            // A colleague's answer must leave the conversation as it found it, so a value it was
            // handed is used and never stored where the shared registry would keep it.
            bool servingConsultation =
                toolContext.Provider.GetVariable(toolContext.Session, Constants.ContextKeys.ServingConsultation) is not null;

            foreach (string parameter in contextParameters)
            {
                // A value that the model passes came from the user on this turn or an earlier one: it is
                // stored before use, so every later turn and every agent sharing it finds it held.
                string? supplied = AsText(arguments.TryGetValue(parameter, out object? argument) ? argument : null);
                if (supplied is not null)
                {
                    arguments[parameter] = supplied;
                    if (servingConsultation)
                        continue;

                    await toolContext.Provider.SetVariableAsync(toolContext.Session, parameter, supplied);

                    logger.LogInformation(
                        Constants.ObservableLogs.ContextSet,
                        Constants.ObservableLogs.ToolName, Name, Constants.ObservableLogs.Set, parameter, supplied);
                    continue;
                }

                // Omitted by the model: the session answers, including for a value that another agent obtained.
                string? held = AsText(toolContext.Provider.GetVariable(toolContext.Session, parameter));
                if (held is not null)
                {
                    arguments[parameter] = held;

                    logger.LogInformation(
                        Constants.ObservableLogs.ContextHit,
                        Constants.ObservableLogs.ToolName, Name, Constants.ObservableLogs.Hit, parameter, held);
                    continue;
                }

                logger.LogInformation(
                    Constants.ObservableLogs.ContextMiss,
                    Constants.ObservableLogs.ToolName, Name, Constants.ObservableLogs.Miss, parameter);
                missingParameters.Add(parameter);
            }

            // Run on a missing value the tool would answer about nobody: the model is told which
            // values are lacking, which are exactly what the user has to be asked for.
            if (missingParameters.Count > 0)
                return new Records.FrameworkToolResult(Constants.ToolResults.ContextValueMissing, new Dictionary<string, string>
                {
                    [Constants.Placeholders.ToolName] = Name,
                    [Constants.Placeholders.MissingValues] = string.Join(", ", missingParameters)
                });

            return await base.InvokeCoreAsync(arguments, cancellationToken);
        }

        /// <summary>
        /// Reads a value as the text that a tool parameter receives; null when it carries none.
        /// </summary>
        /// <remarks>
        /// A model argument and a value restored from a persisted session both arrive as JSON, a
        /// value written this process lifetime as the string it was stored as.
        /// </remarks>
        private static string? AsText(object? value)
        {
            string? text = value switch
            {
                null => null,
                JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => null,
                JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
                JsonElement element => element.GetRawText(),
                _ => Convert.ToString(value, CultureInfo.InvariantCulture)
            };

            // A blank argument is the model naming the parameter without a value, never a value.
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
    }
}
