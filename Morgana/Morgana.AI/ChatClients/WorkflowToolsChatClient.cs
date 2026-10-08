using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Morgana.AI.Providers;
using Morgana.AI.Workflows;

namespace Morgana.AI.ChatClients;

/// <summary>
/// DelegatingChatClient sitting between the model and the tool loop. On every model call it presents the
/// tools that the session's state allows: it keeps the steps of a workflow in order and the colleagues out of
/// reach where a consultation may not happen, by the absence of the tools that do not belong there.
/// </summary>
/// <remarks>
/// Below the tool loop because the tools that a single run is given add to the agent's own instead of
/// replacing them. Read on every call, so a step reached halfway through a turn is offered from the very next call.
/// </remarks>
public sealed class WorkflowToolsChatClient : DelegatingChatClient
{
    /// <summary>Returns the agent's current session, the one whose state decides the tools.</summary>
    private readonly Func<AgentSession?> sessionAccessor;

    /// <summary>The store that the workflow position, the consultation mark and the rounds spent are read from.</summary>
    private readonly MorganaAIContextProvider contextProvider;

    /// <summary>The workflows the agent declares.</summary>
    private readonly IReadOnlyList<Records.WorkflowDefinition> workflows;

    /// <summary>The consultations that one user turn may spend, after which no colleague is offered.</summary>
    private readonly int maxConsultationRoundsPerTurn;

    /// <summary>
    /// Wraps the model the agent's tool loop calls.
    /// </summary>
    /// <param name="innerClient">The client below, which sends the tools to the model.</param>
    /// <param name="sessionAccessor">Returns the agent's current session.</param>
    /// <param name="contextProvider">The agent's context store.</param>
    /// <param name="workflows">The workflows the agent declares.</param>
    /// <param name="maxConsultationRoundsPerTurn">The consultations that one user turn may spend.</param>
    public WorkflowToolsChatClient(
        IChatClient innerClient,
        Func<AgentSession?> sessionAccessor,
        MorganaAIContextProvider contextProvider,
        IReadOnlyList<Records.WorkflowDefinition> workflows,
        int maxConsultationRoundsPerTurn) : base(innerClient)
    {
        this.sessionAccessor = sessionAccessor;
        this.contextProvider = contextProvider;
        this.workflows = workflows;
        this.maxConsultationRoundsPerTurn = maxConsultationRoundsPerTurn;
    }

    /// <inheritdoc/>
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => base.GetResponseAsync(chatMessages, Present(options), cancellationToken);

    /// <inheritdoc/>
    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => base.GetStreamingResponseAsync(chatMessages, Present(options), cancellationToken);

    /// <summary>
    /// Rewrites the options' tools for the state of the session; the caller's options are never mutated.
    /// </summary>
    private ChatOptions? Present(ChatOptions? options)
    {
        AgentSession? session = sessionAccessor();
        if (session is null || options?.Tools is not { Count: > 0 } tools)
            return options;

        bool servingConsultation = contextProvider.GetVariable(session, Constants.ContextKeys.ServingConsultation) is not null;

        // A colleague is not consulted by an agent that is answering one, since the chain stops at one hop,
        // nor by a turn that has spent its rounds.
        bool colleaguesOutOfReach = servingConsultation
            || contextProvider.GetConsultationRounds(session) >= maxConsultationRoundsPerTurn;

        Records.WorkflowPosition? position = workflows.Count == 0 ? null : contextProvider.GetWorkflowPosition(session);
        (Records.WorkflowDefinition Definition, Records.WorkflowStep Step)? running = position?.Resolve(workflows);

        // With no workflow running and every colleague within reach the agent is offered exactly the tools it was given.
        if (running is null && !colleaguesOutOfReach)
            return options;

        List<AITool> presented;
        if (running is null)
        {
            // A consultation answers a colleague and never takes the user's turn, so it never opens a wizard.
            presented = [.. tools.Where(tool => !servingConsultation || tool is not WorkflowLauncherFunction)];
        }
        else
        {
            HashSet<string> signature = running.Value.Definition.ToolSignature();
            presented = [];

            foreach (AITool tool in tools)
            {
                // Closing the turn and consulting a colleague belong to no step: they are offered at every one.
                if (tool.Name == Constants.Tools.Reply && running.Value.Step.Tools.Count > 1)
                    presented.Add(WithStepActions(tool, running.Value.Step.Tools));
                else if (tool.Name == Constants.Tools.Reply
                    || tool.Name.StartsWith(Constants.AgentToAgent.PeerFunctionNamePrefix, StringComparison.Ordinal))
                    presented.Add(tool);
                else if (tool is WorkflowLauncherFunction)
                    continue;
                else if (signature.Contains(tool.Name))
                {
                    // Of the workflow's own tools only the current step's are offered, without what the framework binds.
                    if (running.Value.Step.Tools.Contains(tool.Name))
                        presented.Add(WithoutBoundParameters(tool, position!.Arguments.Keys));
                }

                // Outside the signature a tool is a private method of the workflow: offered for answers
                // on the side, except where it needs approval, since a binding act there would be a second workflow.
                else if (tool is not ApprovalRequiredAIFunction)
                    presented.Add(tool);
            }
        }

        if (colleaguesOutOfReach)
            presented.RemoveAll(tool => tool.Name.StartsWith(Constants.AgentToAgent.PeerFunctionNamePrefix, StringComparison.Ordinal));

        ChatOptions presentedOptions = options.Clone();
        presentedOptions.Tools = presented;

        return presentedOptions;
    }

    /// <summary>
    /// Takes the parameters that the framework fills in out of the schema the model reads.
    /// </summary>
    /// <param name="tool">A tool of the current step.</param>
    /// <param name="boundParameters">The parameters that the step binds.</param>
    private static AITool WithoutBoundParameters(AITool tool, IEnumerable<string> boundParameters)
    {
        if (tool is not AIFunction function)
            return tool;

        JsonObject schema = SchemaOf(function);
        // A workflow carries its property's name while a tool spells its parameter its own way, so the
        // schema's own spelling is what is hidden.
        string[] hidden = schema["properties"] is JsonObject properties
            ? [.. properties.Select(property => property.Key)
                .Where(name => boundParameters.Contains(name, StringComparer.OrdinalIgnoreCase))]
            : [];
        if (hidden.Length == 0)
            return tool;

        foreach (string parameter in hidden)
            schema["properties"]!.AsObject().Remove(parameter);

        if (schema["required"] is JsonArray required)
            schema["required"] = new JsonArray([.. required
                .Where(name => !hidden.Contains(name!.GetValue<string>(), StringComparer.Ordinal))
                .Select(name => name!.DeepClone())]);

        return Rewritten(function, schema);
    }

    /// <summary>
    /// Turns the actions of <c>Reply</c> into the proposal of a choice step: required, one per tool of the step and each leading to one of them.
    /// </summary>
    /// <param name="reply">The <c>Reply</c> tool as the agent holds it.</param>
    /// <param name="stepTools">The tools of the choice step, in declared order.</param>
    private static AITool WithStepActions(AITool reply, IReadOnlyList<string> stepTools)
    {
        if (reply is not AIFunction function)
            return reply;

        JsonObject schema = SchemaOf(function);
        JsonObject actions = schema["properties"]!["actions"]!.AsObject();

        // A null in the place of the array would leave the step with no button, so the type is narrowed to the array.
        actions["type"] = "array";
        actions.Remove("default");
        actions["minItems"] = stepTools.Count;
        actions["maxItems"] = stepTools.Count;
        actions["items"]!["properties"]!["tool"]!.AsObject()["enum"] = new JsonArray([.. stepTools.Select(tool => (JsonNode)JsonValue.Create(tool)!)]);

        JsonArray required = schema["required"] as JsonArray ?? [];
        if (!required.Any(name => name!.GetValue<string>() == "actions"))
            required.Add("actions");
        schema["required"] = required;

        return Rewritten(function, schema);
    }

    /// <summary>The schema of a tool, parsed for editing.</summary>
    private static JsonObject SchemaOf(AIFunction function)
        => JsonNode.Parse(function.JsonSchema.GetRawText())!.AsObject();

    /// <summary>
    /// Presents a tool under an edited schema.
    /// </summary>
    private static AITool Rewritten(AIFunction function, JsonObject schema)
    {
        // Only a declaration reaches the model from here: the tool loop above runs the original function.
        // The approval wrapper is put back on top because the client below recognizes a tool needing approval by it.
        AIFunction rewritten = new SchemaRewrittenFunction(function, schema);
        return function is ApprovalRequiredAIFunction ? new ApprovalRequiredAIFunction(rewritten) : rewritten;
    }

    /// <summary>
    /// A tool presented under a schema edited for the current step.
    /// </summary>
    private sealed class SchemaRewrittenFunction : DelegatingAIFunction
    {
        /// <summary>The schema as edited.</summary>
        private readonly JsonElement jsonSchema;

        /// <summary>
        /// Wraps a tool of the current step.
        /// </summary>
        /// <param name="innerFunction">The tool as the agent holds it.</param>
        /// <param name="schema">The edited schema that the model reads.</param>
        public SchemaRewrittenFunction(AIFunction innerFunction, JsonObject schema) : base(innerFunction)
            => jsonSchema = JsonSerializer.SerializeToElement(schema);

        /// <inheritdoc />
        public override JsonElement JsonSchema => jsonSchema;
    }
}
