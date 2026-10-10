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
        // The three collaborators together are what let a tool's context parameters come from the session.
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
        // A name serves one tool: a second registration would silently replace the first.
        if (!toolMethods.TryAdd(toolName, toolMethod))
            throw new InvalidOperationException($"Tool '{toolName}' already registered");

        // A definition that disagrees with its method is refused when the agent is built, not on the first call.
        ValidateToolDefinition(toolMethod, definition);
        toolDefinitions[toolName] = definition;

        // Registrations chain.
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
        // The method and the definition were registered together, so a name that finds one finds the other.
        Delegate implementation = ResolveTool(toolName);
        Records.ToolDefinition toolDefinition = toolDefinitions.TryGetValue(toolName, out Records.ToolDefinition? def)
            ? def
            : throw new InvalidOperationException($"Tool definition '{toolName}' not found");

        // The model reads a parameter's description only through the schema, so each one is looked up by name there.
        Dictionary<string, string> parameterDescriptions = toolDefinition.Parameters.ToDictionary(p => p.Name, p => p.Description);

        // The tool as the model sees it: the composed description when a composer is present and the authored one otherwise.
        AIFunction function = AIFunctionFactory.Create(implementation,
            new AIFunctionFactoryOptions
            {
                Name = toolDefinition.Name,
                Description = promptComposerService is null
                    ? toolDefinition.Description
                    : await promptComposerService.ComposeToolDescriptionAsync(toolDefinition),
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

        // The parameters that the framework fills from the session instead of asking the model for them.
        string[] contextParameters = [.. toolDefinition.Parameters
            .Where(p => string.Equals(p.Scope?.Trim(), Constants.Scopes.Context, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Name)];

        // A context-scoped tool on an adapter with no session has nowhere to resolve its inputs from:
        // a wiring fault, refused at agent creation rather than at the first call.
        if (contextParameters.Length > 0 && (toolContextFactory is null || logger is null))
            throw new InvalidOperationException($"Tool '{toolName}' declares context-scoped parameters but its adapter holds no session to resolve them from");

        // A tool resolving nothing from the session reaches the model exactly as declared.
        AIFunction resolvedFunction = contextParameters.Length == 0
            ? function
            : new ContextResolvingFunction(function, contextParameters, toolContextFactory!, logger!);

        // A tool that changes something real waits for the user's approval of the exact call before it
        // runs. Outermost, so nothing of the call is resolved or stored until the user has approved it.
        return toolDefinition.RequiresExecutionApproval
            ? new ApprovalRequiredAIFunction(resolvedFunction)
            : resolvedFunction;
    }

    /// <summary>
    /// Creates AIFunction instances for all registered tools.
    /// </summary>
    /// <returns>Enumerable of AIFunction instances ready for agent use</returns>
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
        // The parameters that a call lands on, against the ones that the model is shown.
        ParameterInfo[] methodParams = implementation.Method.GetParameters();
        List<Records.ToolParameter> definitionParams = [.. definition.Parameters];

        // The model is shown the definition and the call lands on the method, so the two must list the same parameters.
        if (methodParams.Length != definitionParams.Count)
            throw new ArgumentException($"Parameter count mismatch: method has {methodParams.Length}, definition has {definitionParams.Count}");

        foreach (ParameterInfo methodParam in methodParams)
        {
            // A call binds its arguments by name, so a method parameter missing from the definition could never be passed.
            Records.ToolParameter defParam = definitionParams.FirstOrDefault(p => p.Name == methodParam.Name)
                                             ?? throw new ArgumentException($"Parameter '{methodParam.Name}' not found in definition");

            // A parameter that the model must supply cannot be one that the method lets it omit.
            if (defParam.Required && methodParam.HasDefaultValue)
                throw new ArgumentException($"Parameter '{methodParam.Name}' is required in definition but optional in method");
        }
    }

    /// <summary>
    /// A tool whose context-scoped parameters the framework resolves: a value the model passes is
    /// stored and used, one it omits is read from the session and one the session lacks keeps the
    /// tool from running at all.
    /// </summary>
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
            // The wrapper keeps what it needs to resolve the parameters at each invocation.
            this.contextParameters = contextParameters;
            this.toolContextFactory = toolContextFactory;
            this.logger = logger;

            // The C# method still requires every one of them: the framework fills them in before
            // the method is reached, so only the model is released from supplying them.
            // The schema is a generated object, so it always parses as one.
            JsonObject schema = JsonNode.Parse(innerFunction.JsonSchema.GetRawText())!.AsObject();
            if (schema["required"] is JsonArray required)
                schema["required"] = new JsonArray([.. required
                    .Where(name => !contextParameters.Contains(name!.GetValue<string>(), StringComparer.Ordinal))
                    .Select(name => name!.DeepClone())]);
            // The relaxed schema is what the model is shown for the tool's whole life.
            jsonSchema = JsonSerializer.SerializeToElement(schema);
        }

        /// <inheritdoc />
        public override JsonElement JsonSchema => jsonSchema;

        /// <summary>
        /// Resolves every context-scoped parameter, then runs the tool only when all of them hold a value.
        /// </summary>
        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            // The factory is read at the call, since the session it points to belongs to the turn in flight.
            MorganaTool.ToolContext toolContext = toolContextFactory();

            // The values that neither the model nor the session could supply, which the user has to be asked for.
            List<string> missingParameters = [];

            // The model's call stays on the record as the model wrote it: a value the session supplies reaches
            // the tool alone. Written into the call, it would read on a later turn as a value the model made up.
            AIFunctionArguments argumentsForTool = new AIFunctionArguments(new Dictionary<string, object?>(arguments))
            {
                Services = arguments.Services,
                Context = arguments.Context
            };

            // A colleague's answer must leave the conversation as it found it, so a value it was
            // handed is used and never stored where the shared registry would keep it.
            bool servingConsultation =
                toolContext.Provider.GetVariable(toolContext.Session, Constants.ContextKeys.ServingConsultation) is not null;

            foreach (string parameter in contextParameters)
            {
                // A value that the model passes came from the user on this turn or an earlier one: it is
                // stored before use, so every later turn and every agent sharing it finds it held.
                string? supplied = AsText(arguments.GetValueOrDefault(parameter));
                if (supplied is not null)
                {
                    // The tool receives the value as text, the form in which the context keeps it.
                    argumentsForTool[parameter] = supplied;

                    // A colleague's answer uses the value without leaving it behind.
                    if (servingConsultation)
                        continue;

                    // Held by this agent's session and, for a parameter declared shared, by the conversation's registry too.
                    await toolContext.Provider.SetVariableAsync(toolContext.Session, parameter, supplied);

                    // The harness reads this line to observe that the value was stored.
                    logger.LogInformation(
                        Constants.ObservableLogs.ContextSet,
                        Constants.ObservableLogs.ToolName, Name, Constants.ObservableLogs.Set, parameter, supplied);
                    continue;
                }

                // Omitted by the model: the session answers, including for a value that another agent obtained.
                string? held = AsText(toolContext.Provider.GetVariable(toolContext.Session, parameter));
                if (held is not null)
                {
                    // The session's value takes the place of the one the model left out.
                    argumentsForTool[parameter] = held;

                    // The harness reads this line to observe that the session answered.
                    logger.LogInformation(
                        Constants.ObservableLogs.ContextHit,
                        Constants.ObservableLogs.ToolName, Name, Constants.ObservableLogs.Hit, parameter, held);
                    continue;
                }

                // Neither the model nor the session holds it: the harness reads this line and the user is asked below.
                logger.LogInformation(
                    Constants.ObservableLogs.ContextMiss,
                    Constants.ObservableLogs.ToolName, Name, Constants.ObservableLogs.Miss, parameter);

                // Every lacking value is gathered before the tool is refused, so the user is asked for all of them at once.
                missingParameters.Add(parameter);
            }

            // Run on a missing value the tool would answer about nobody: the model is told which
            // values are lacking, which are exactly what the user has to be asked for.
            if (missingParameters.Count > 0)
                return new Records.FrameworkToolResult(Constants.ToolInjections.ContextValueMissing, new Dictionary<string, string>
                {
                    [Constants.Placeholders.ToolName] = Name,
                    [Constants.Placeholders.MissingValues] = string.Join(", ", missingParameters)
                });

            // Every parameter holds a value: the tool runs on the resolved arguments.
            return await base.InvokeCoreAsync(argumentsForTool, cancellationToken);
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
                // Nothing was passed or stored.
                null => null,

                // A JSON null is the parameter named without a value.
                JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => null,

                // A JSON string is the text itself, without its quotes.
                JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),

                // A number or a boolean keeps the spelling that the model or the session gave it.
                JsonElement element => element.GetRawText(),

                // A value stored in this process is spelled the same on every host.
                _ => Convert.ToString(value, CultureInfo.InvariantCulture)
            };

            // A blank argument is the model naming the parameter without a value, never a value.
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
    }
}