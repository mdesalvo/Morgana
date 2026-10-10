using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Morgana.AI.Providers;
using Morgana.AI.Workflows;

namespace Morgana.AI.ChatClients;

/// <summary>
/// DelegatingChatClient sitting between the model and the tool loop. On every model call it presents the
/// tools that the session's state allows: it keeps a workflow's entry behind its launcher, its steps in order and
/// the colleagues out of reach where a consultation may not happen, by the absence of the tools that do not belong there.
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
        => base.GetResponseAsync(chatMessages, NarrowToSession(options), cancellationToken);

    /// <inheritdoc/>
    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => base.GetStreamingResponseAsync(chatMessages, NarrowToSession(options), cancellationToken);

    /// <summary>
    /// Rewrites the options' tools for the state of the session; the caller's options are never mutated.
    /// </summary>
    private ChatOptions? NarrowToSession(ChatOptions? options)
    {
        // The session is read on every call: a step reached halfway through a turn changes what the next call offers.
        AgentSession? session = sessionAccessor();

        // A call with no session or no tools has nothing to present, so the caller's options go through as they are.
        if (session is null || options?.Tools is not { Count: > 0 } tools)
            return options;

        // An agent answering a colleague carries the consultation mark in its session.
        bool isServingConsultation = contextProvider.GetVariable(session, Constants.ContextKeys.ServingConsultation) is not null;

        // A colleague is not consulted by an agent that is answering one, since the chain stops at one hop,
        // nor by a turn that has spent its rounds.
        bool colleaguesOutOfReach = isServingConsultation
            || contextProvider.GetConsultationRounds(session) >= maxConsultationRoundsPerTurn;

        // The position says whether a workflow is running and at which step; an agent with no workflows holds none.
        Records.WorkflowPosition? position = workflows.Count == 0 ? null : contextProvider.GetWorkflowPosition(session);
        (Records.WorkflowDefinition Definition, Records.WorkflowStep Step)? runningWorkflow = position?.Resolve(workflows);

        // With no workflow declared and every colleague within reach the agent is offered exactly the tools it was given.
        if (workflows.Count == 0 && !colleaguesOutOfReach)
            return options;

        // The tools the model is offered on this call, each one weighed against the state of the session.
        List<AITool> offeredTools = [];
        foreach (AITool tool in tools)
        {
            // The workflows decide whether the tool belongs to the step that stands: entry tools, launchers, other steps' tools and approval tools stay out.
            if (!WorkflowEngine.IsOffered(tool.Name, tool is WorkflowLauncherFunction, tool is ApprovalRequiredAIFunction, runningWorkflow, workflows))
                continue;

            // The ban on colleagues holds inside a running workflow as well, whatever the step allows.
            if (colleaguesOutOfReach && tool.Name.StartsWith(Constants.AgentToAgent.PeerFunctionNamePrefix, StringComparison.Ordinal))
                continue;

            // A consultation answers a colleague and never takes the user's turn, so it never opens a wizard.
            if (isServingConsultation && tool is WorkflowLauncherFunction)
                continue;

            // At a choice step Reply is offered with the step's tools as the only actions it may propose.
            if (runningWorkflow is { Step.Tools.Count: > 1 } && tool.Name == Constants.Tools.Reply)
                offeredTools.Add(WithStepActions(tool, runningWorkflow.Value.Step.Tools));

            // A tool of the current step is offered without what the framework binds.
            else if (runningWorkflow is { } workflowAndStep && workflowAndStep.Step.Tools.Contains(tool.Name))
                offeredTools.Add(WithoutBoundParameters(tool, position!.Arguments.Keys));

            // Any other tool that the workflows let through is offered as the agent holds it.
            else
                offeredTools.Add(tool);
        }

        // The caller's options are shared across turns, so the narrowed tool list goes on a clone.
        ChatOptions offeredOptions = options.Clone();
        offeredOptions.Tools = offeredTools;

        // The model reads only the tools that the session's state allows.
        return offeredOptions;
    }

    /// <summary>
    /// Takes the parameters that the framework fills in out of the schema the model reads.
    /// </summary>
    /// <param name="tool">A tool of the current step.</param>
    /// <param name="boundParameters">The parameters that the step binds.</param>
    private static AITool WithoutBoundParameters(AITool tool, IEnumerable<string> boundParameters)
    {
        // A tool that is not a function has no schema to edit.
        if (tool is not AIFunction function)
            return tool;

        // The schema of the tool is read, so that the parameters the step binds can be hidden from the model.
        JsonObject schema = SchemaOf(function);

        // A workflow carries its property's name while a tool spells its parameter its own way, so the
        // schema's own spelling is what is hidden.
        string[] hidden = schema["properties"] is JsonObject properties
            ? [.. properties.Select(property => property.Key)
                .Where(name => boundParameters.Contains(name, StringComparer.OrdinalIgnoreCase))]
            : [];
        // A step that binds nothing of this tool leaves its schema as the tool declared it.
        if (hidden.Length == 0)
            return tool;

        // The model never supplies a value that the framework already holds, so the parameter is not offered.
        foreach (string parameter in hidden)
            schema["properties"]!.AsObject().Remove(parameter);

        // A hidden parameter cannot stay required. Otherwise the model could never satisfy the schema.
        if (schema["required"] is JsonArray required)
            schema["required"] = new JsonArray([.. required
                .Where(name => !hidden.Contains(name!.GetValue<string>(), StringComparer.Ordinal))
                .Select(name => name!.DeepClone())]);

        // The tool goes on offer under the schema without the bound parameters.
        return Rewritten(function, schema);
    }

    /// <summary>
    /// Turns the actions of <c>Reply</c> into the proposal of a choice step: required, one per tool of the step and each leading to one of them.
    /// </summary>
    /// <param name="reply">The <c>Reply</c> tool as the agent holds it.</param>
    /// <param name="stepTools">The tools of the choice step, in declared order.</param>
    private static AITool WithStepActions(AITool reply, IReadOnlyList<string> stepTools)
    {
        // A Reply that is not a function has no schema to edit.
        if (reply is not AIFunction replyFunction)
            return reply;

        // The schema of Reply is read, so that the choices of the step can be written into its actions.
        JsonObject schema = SchemaOf(replyFunction);

        // The actions property is where Reply offers buttons; its schema becomes the proposal of the step's choices.
        JsonObject actions = schema["properties"]!["actions"]!.AsObject();

        // A null in the place of the array would leave the step with no button, so the type is narrowed to the array.
        actions["type"] = "array";
        actions.Remove("default");

        // One button per tool of the step: the user chooses among exactly the moves that the step allows.
        actions["minItems"] = stepTools.Count;
        actions["maxItems"] = stepTools.Count;
        actions["items"]!["properties"]!["tool"]!.AsObject()["enum"] = new JsonArray([.. stepTools.Select(tool => (JsonNode)JsonValue.Create(tool)!)]);

        // Reply declares actions optional, but a choice step cannot be proposed without them.
        JsonArray required = schema["required"] as JsonArray ?? [];
        if (required.All(name => name!.GetValue<string>() != "actions"))
            required.Add("actions");
        schema["required"] = required;

        // The model reads Reply with its actions fixed to the step's tools.
        return Rewritten(replyFunction, schema);
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
