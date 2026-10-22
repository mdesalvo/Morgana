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
/// A pressed button is a call: on the turn of the press it answers in the model's place with that call, so the tool loop
/// runs it whatever the model would have done. The turn then goes on to its answer.
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
        // A press whose call is due is answered with that call and no model is asked: the call is the press's own.
        => DuePressedCall(options) is { } pressedCall
            ? Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [pressedCall])) { FinishReason = ChatFinishReason.ToolCalls })
            : base.GetResponseAsync(chatMessages, NarrowTools(options), cancellationToken);

    /// <inheritdoc/>
    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        // The streaming turn receives the same call, as the one update of a response that no model wrote.
        => DuePressedCall(options) is { } pressedCall
            ? new[] { new ChatResponseUpdate(ChatRole.Assistant, [pressedCall]) { FinishReason = ChatFinishReason.ToolCalls } }.ToAsyncEnumerable()
            : base.GetStreamingResponseAsync(chatMessages, NarrowTools(options), cancellationToken);

    /// <summary>
    /// The call that a button pressed on this turn makes, when the turn has not made it yet; null on any other call.
    /// </summary>
    /// <remarks>
    /// A press is the user's own choice of the call, so the model is not asked whether to make it: the tool loop receives it
    /// as a response, runs it through the agent's invoker like any other call and hands the model its result.
    /// </remarks>
    private FunctionCallContent? DuePressedCall(ChatOptions? options)
    {
        // Only a session that holds a press not yet made on this turn has a call due.
        AgentSession? session = sessionAccessor();
        if (session is null || contextProvider.IsPressedCallMade(session) || contextProvider.GetPressedCall(session) is not { } pressedCall)
            return null;

        // A call waiting for the user's typed answer is the model's to compose from it, so nothing is due here.
        if (pressedCall.AwaitingAnswer)
            return null;

        // A button can only lead to a tool that the agent holds; one it no longer holds makes no call and the turn runs as typed.
        if (options?.Tools?.Any(tool => string.Equals(tool.Name, pressedCall.Tool, StringComparison.Ordinal)) != true)
            return null;

        // The call carries the button's arguments as the values that a model would have passed.
        Dictionary<string, object?> arguments = pressedCall.Arguments.ToDictionary(
            argument => argument.Key,
            argument => (object?)JsonDocument.Parse(argument.Value).RootElement.Clone(),
            StringComparer.Ordinal);

        // Each press is a call of its own, told from every other call of the history by its id.
        return new FunctionCallContent($"press_{Guid.NewGuid():N}", pressedCall.Tool, arguments);
    }

    /// <summary>
    /// Rewrites the options' tools for the state of the session; the caller's options are never mutated.
    /// </summary>
    private ChatOptions? NarrowTools(ChatOptions? options)
    {
        // The session is read on every call: a step reached halfway through a turn changes what the next call offers.
        AgentSession? session = sessionAccessor();

        // A call with no session or no tools has nothing to present, so the caller's options go through as they are.
        if (session is null || options?.Tools is not { Count: > 0 } tools)
            return options;

        // An agent answering a colleague carries the consultation mark in its session.
        bool isServingConsultation = contextProvider.HoldsFrameworkKey(session, Constants.ContextKeys.ServingConsultation);

        // A colleague is not consulted by an agent that is answering one, since the chain stops at one hop,
        // nor by a turn that has spent its rounds.
        bool colleaguesOutOfReach = isServingConsultation
            || contextProvider.GetConsultationRounds(session) >= maxConsultationRoundsPerTurn;

        // The position says whether a workflow is running and at which step; an agent with no workflows holds none.
        Records.WorkflowPosition? position = workflows.Count == 0 ? null : contextProvider.GetWorkflowPosition(session);
        (Records.WorkflowDefinition Definition, Records.WorkflowStep Step)? runningWorkflow = position?.Resolve(workflows);

        // A landing is the user's choice of this agent and nothing else: the turn holds Reply alone, so the model
        // introduces itself from the declaration that its context provider adds and answers through Reply.
        if (contextProvider.HoldsFrameworkKey(session, Constants.ContextKeys.Landing))
        {
            // The clone carries Reply alone, whatever else the agent holds, without actions: the landing closes on the menu.
            ChatOptions landingOptions = options.Clone();
            landingOptions.Tools = [.. tools.Where(tool => tool.Name is Constants.Tools.Reply).Select(WithoutActions)];

            // The landing's options go to the model with no tool mode: no provider is ever made to call a tool.
            return landingOptions;
        }

        // A press whose call waits for the user's typed answer keeps that call on offer beside every other tool of the agent.
        // The values that the button or the step already supply are left out of it, so the model composes the call from the answer
        // or answers whatever else the user wrote.
        bool pressedCallMade = contextProvider.IsPressedCallMade(session);

        // Null unless a call awaits the user's answer on this turn: then it holds the form in which that call is offered.
        AITool? awaitingToolOffer = null;
        if (!pressedCallMade
            && contextProvider.GetPressedCall(session) is { AwaitingAnswer: true } pressedCall
            && tools.FirstOrDefault(tool => string.Equals(tool.Name, pressedCall.Tool, StringComparison.Ordinal)) is { } pressedTool)
        {
            // The step binds its own values for a tool of the step, the button supplies the rest of what it named.
            IEnumerable<string> boundValueNames = runningWorkflow?.Step.Tools.Contains(pressedCall.Tool) == true
                ? [.. pressedCall.Arguments.Keys, .. position!.Arguments.Keys]
                : pressedCall.Arguments.Keys;

            // The awaiting call is offered with what the framework binds out of its schema, in place of the tool as the agent holds it.
            awaitingToolOffer = WithoutBoundParameters(pressedTool, boundValueNames);
        }

        // Once the pressed call is made the turn has done what the press asked: outside a workflow, what remains is the answer.
        // A workflow that the call started or moved on still offers its step, which is how the procedure goes on.
        if (pressedCallMade && runningWorkflow is null)
        {
            // The clone carries Reply alone, as a landing's does: no workflow runs, so the turn closes on the menu.
            ChatOptions answerOptions = options.Clone();
            answerOptions.Tools = [.. tools.Where(tool => tool.Name is Constants.Tools.Reply).Select(WithoutActions)];

            // The model reads Reply alone and answers on the result of the call.
            return answerOptions;
        }

        // With no workflow declared and every colleague within reach the agent is offered exactly the tools it was given,
        // unless a call awaits the user's answer: its own form must still be put on offer.
        if (workflows.Count == 0 && !colleaguesOutOfReach && awaitingToolOffer is null)
            return options;

        // The tools the model is offered on this call, each one weighed against the state of the session.
        List<AITool> offeredTools = [];
        foreach (AITool tool in tools)
        {
            // The awaiting call is offered in the form that leaves the model only the values the user answers, whatever the step would otherwise allow.
            if (awaitingToolOffer is not null && string.Equals(tool.Name, awaitingToolOffer.Name, StringComparison.Ordinal))
            {
                offeredTools.Add(awaitingToolOffer);
                continue;
            }

            // The workflows decide whether the tool belongs to the step that stands: entry tools, launchers, other steps' tools and approval tools stay out.
            if (!WorkflowEngine.IsOffered(tool.Name, tool is WorkflowLauncherFunction, tool is ApprovalRequiredAIFunction, runningWorkflow, workflows))
                continue;

            // The ban on colleagues holds inside a running workflow as well, whatever the step allows.
            if (colleaguesOutOfReach && tool.Name.StartsWith(Constants.AgentToAgent.PeerFunctionNamePrefix, StringComparison.Ordinal))
                continue;

            // A consultation answers a colleague and never takes the user's turn, so it never opens a wizard.
            if (isServingConsultation && tool is WorkflowLauncherFunction)
                continue;

            // Each tool is offered in the form that the running workflow allows, as it is held where no workflow runs.
            switch (runningWorkflow)
            {
                // At a choice step Reply is offered with the step's tools as the only actions it may propose.
                case { Step.Tools.Count: > 1 } when tool.Name == Constants.Tools.Reply:
                    offeredTools.Add(WithStepActions(tool, runningWorkflow.Value.Step.Tools));
                    break;
                // A tool of the current step is offered without what the framework binds.
                case { } workflowAndStep when workflowAndStep.Step.Tools.Contains(tool.Name):
                    offeredTools.Add(WithoutBoundParameters(tool, position!.Arguments.Keys));
                    break;
                // Anywhere but a choice step the buttons are the framework's, so Reply is offered without actions.
                case var _ when tool.Name == Constants.Tools.Reply:
                    offeredTools.Add(WithoutActions(tool));
                    break;
                // Any other tool that the workflows let through is offered as the agent holds it.
                default:
                    offeredTools.Add(tool);
                    break;
            }
        }

        // The caller's options are shared across turns, so the narrowedOptions tool list goes on a clone.
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
        return PresentUnderSchema(function, schema);
    }

    /// <summary>Offers <c>Reply</c> with no actions, for a turn whose buttons the framework writes.</summary>
    /// <param name="reply">The <c>Reply</c> tool as the agent holds it.</param>
    private static AITool WithoutActions(AITool reply)
        // The buttons of such a turn are bound by the framework as a step's values are, so the model is never asked for them.
        => WithoutBoundParameters(reply, [Constants.Tools.ReplyParameters.Actions]);

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
        JsonObject actions = schema["properties"]![Constants.Tools.ReplyParameters.Actions]!.AsObject();

        // A null in the place of the array would leave the step with no button, so the type is narrowedOptions to the array.
        actions["type"] = "array";
        actions.Remove("default");

        // One button per tool of the step: the user chooses among exactly the moves that the step allows.
        actions["minItems"] = stepTools.Count;
        actions["maxItems"] = stepTools.Count;
        actions["items"]!["properties"]!["tool"]!.AsObject()["enum"] = new JsonArray([.. stepTools.Select(JsonNode (tool) => JsonValue.Create(tool)!)]);

        // Reply declares actions optional, but a choice step cannot be proposed without them.
        JsonArray required = schema["required"] as JsonArray ?? [];
        if (required.All(name => name!.GetValue<string>() != Constants.Tools.ReplyParameters.Actions))
            required.Add(Constants.Tools.ReplyParameters.Actions);
        schema["required"] = required;

        // The model reads Reply with its actions fixed to the step's tools.
        return PresentUnderSchema(replyFunction, schema);
    }

    /// <summary>The schema of a tool, parsed for editing.</summary>
    internal static JsonObject SchemaOf(AIFunction function)
        => JsonNode.Parse(function.JsonSchema.GetRawText())!.AsObject();

    /// <summary>
    /// Presents a tool under an edited schema.
    /// </summary>
    internal static AIFunction PresentUnderSchema(AIFunction function, JsonObject schema)
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
