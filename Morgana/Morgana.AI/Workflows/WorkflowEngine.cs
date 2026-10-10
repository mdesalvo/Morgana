using System.Text.Json;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;

namespace Morgana.AI.Workflows;

/// <summary>
/// The state machine of one agent's workflows: it says which step follows a tool's outcome and which
/// values the next step has bound, while the agent keeps talking to the user.
/// </summary>
/// <remarks>
/// Workflow, checkpoint manager and store are rebuilt from the position's checkpoint on every call, so the
/// engine holds no state between turns: what survives a turn or a restart is the position alone.
/// </remarks>
public sealed class WorkflowEngine
{
    /// <summary>Prefixes the id of each step's port, which shares its namespace with the executors.</summary>
    private const string PortPrefix = "step-";

    /// <summary>Prefixes the id of each step's router.</summary>
    private const string RouterPrefix = "route-";

    /// <summary>The workflows of the agent, by name.</summary>
    private readonly Dictionary<string, Records.WorkflowDefinition> definitions;

    /// <summary>
    /// Initializes the engine over the workflows of one agent.
    /// </summary>
    /// <param name="definitions">The workflows the agent declares, already validated at startup.</param>
    public WorkflowEngine(IEnumerable<Records.WorkflowDefinition> definitions)
        => this.definitions = definitions.ToDictionary(definition => definition.Name, StringComparer.Ordinal);

    /// <summary>
    /// Starts a workflow and stops it at its first step, waiting for the agent's outcome.
    /// </summary>
    /// <param name="workflowName">The workflow to start.</param>
    /// <returns>Where the workflow stands: its first step.</returns>
    public async Task<Records.WorkflowPosition> LaunchAsync(string workflowName)
    {
        // A launcher exists only for a workflow that the agent declares, so the name always finds its definition.
        Records.WorkflowDefinition definition = definitions[workflowName];

        // Every launch is a run of its own: a workflow started again after it ended shares nothing with the earlier one.
        LatestCheckpointStore store = new LatestCheckpointStore();
        await using Run run = await InProcessExecution.RunAsync(
            Build(definition), workflowName, CheckpointManager.CreateJson(store), Guid.NewGuid().ToString("N"));

        // The start executor always hands its prompt to the first port, so a run that raised no request is a broken graph.
        ExternalRequest request = Observe(run).Pending
            ?? throw new InvalidOperationException($"Workflow '{workflowName}' ended without reaching its first step");

        // The session keeps the first step together with the checkpoint that the next outcome resumes from.
        return ToPosition(workflowName, request, store.Export(run.LastCheckpoint!));
    }

    /// <summary>
    /// Hands the engine the outcome of the call made at the current step and moves the workflow on.
    /// </summary>
    /// <param name="position">Where the workflow stands; its checkpoint is where the engine resumes from.</param>
    /// <param name="outcome">The tool that was called and how its call ended.</param>
    /// <returns>Where the workflow stands now; <c>null</c> when the outcome ended it.</returns>
    public async Task<Records.WorkflowPosition?> AdvanceAsync(Records.WorkflowPosition position, Records.StepOutcome outcome)
    {
        // A position is written only by this engine for one of its workflows, so its name always finds the definition.
        Records.WorkflowDefinition definition = definitions[position.Workflow];

        // The run is rebuilt on the checkpoint that the session kept, which is the only memory the workflow has.
        (LatestCheckpointStore store, CheckpointInfo checkpoint) = LatestCheckpointStore.Import(position.Checkpoint);

        await using Run run = await InProcessExecution.ResumeAsync(Build(definition), checkpoint, CheckpointManager.CreateJson(store));

        // The step is read from the port that the engine is waiting at: a position that disagrees is a stale session.
        ExternalRequest waiting = Observe(run).Pending
            ?? throw new InvalidOperationException($"Workflow '{position.Workflow}' has no step waiting for an outcome");
        if (!string.Equals(StepOf(waiting), position.Step, StringComparison.Ordinal))
            throw new InvalidOperationException($"Workflow '{position.Workflow}' waits at '{StepOf(waiting)}' while its position says '{position.Step}'");

        // The outcome is the answer to the waiting step: its router picks the edge and the run moves to the next port.
        await run.ResumeAsync([waiting.CreateResponse(outcome)]);

        // No pending request after the outcome means the router sent nothing on: the workflow ended.
        (ExternalRequest? Pending, CheckpointInfo? Checkpoint) next = Observe(run);
        return next.Pending is null ? null : ToPosition(position.Workflow, next.Pending, store.Export(next.Checkpoint!));
    }

    /// <summary>
    /// Reads how a call ended from the text of the tool's result.
    /// </summary>
    /// <param name="tool">The tool that was called.</param>
    /// <param name="resultText">The result as the text the model reads.</param>
    /// <param name="isMCPTool">Whether the tool comes from an MCP server: the origin decides how the result is read, whatever its shape.</param>
    /// <returns>The outcome, whose fields are where an edge reads the values that it carries.</returns>
    public static Records.StepOutcome ReadOutcome(string tool, string resultText, bool isMCPTool)
    {
        // A native tool returns its typed record bare: the record is the fields and its error field marks a failure.
        if (!isMCPTool)
            return new Records.StepOutcome(tool, ReadField(resultText, Constants.Workflows.FailureField) is not null, resultText);

        try
        {
            // The text is the protocol's envelope, as the MCP client handed it to the model.
            using JsonDocument document = JsonDocument.Parse(resultText);
            JsonElement envelope = document.RootElement;

            // A server that answered with something other than the protocol's envelope reports neither a failure nor a field.
            if (envelope.ValueKind != JsonValueKind.Object)
                return new Records.StepOutcome(tool, false, null);

            // An MCP result is the protocol's envelope: the record sits one level down, under structuredContent.
            string? fields = envelope.TryGetProperty(Constants.Workflows.MCPStructuredContent, out JsonElement record)
                             && record.ValueKind == JsonValueKind.Object
                ? record.GetRawText()
                : null;

            // A server reports a failure either on the envelope or, like a native tool, in the record's failure field.
            bool reportedByServer = envelope.TryGetProperty(Constants.Workflows.MCPIsError, out JsonElement isError)
                                    && isError.ValueKind == JsonValueKind.True;
            bool failed = reportedByServer || (fields is not null && ReadField(fields, Constants.Workflows.FailureField) is not null);

            return new Records.StepOutcome(tool, failed, fields);
        }
        catch (JsonException)
        {
            // A text that is not JSON holds neither a failure marker nor a field.
            return new Records.StepOutcome(tool, false, null);
        }
    }

    /// <summary>
    /// Reads one field of a tool's result, however the record cased it.
    /// </summary>
    /// <param name="resultJson">The tool's result as the JSON text the model read.</param>
    /// <param name="field">The field by the name that a workflow edge carries it under.</param>
    /// <returns>The field's JSON text; <c>null</c> when the field is absent or null or the result is not an object.</returns>
    public static string? ReadField(string resultJson, string field)
    {
        try
        {
            // The text is the record as the model read it, which is the only copy of the result that the engine sees.
            using JsonDocument document = JsonDocument.Parse(resultJson);

            // Only a record has fields: a bare value or a list carries nothing an edge could name.
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            // An edge names the C# property while the JSON may case it otherwise, so the match ignores case.
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                // A field written as null is a value the tool did not produce, the same as a missing one.
                if (string.Equals(property.Name, field, StringComparison.OrdinalIgnoreCase))
                    return property.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : property.Value.GetRawText();
            }

            // The record does not declare the field: the edge carries nothing for it.
            return null;
        }
        catch (JsonException)
        {
            // A result that is not JSON holds no field: a source that cannot be read leaves its parameter unbound.
            return null;
        }
    }

    /// <summary>
    /// Builds the Microsoft workflow of one definition: a port per step waiting for the agent's outcome and a
    /// router per step choosing the next one.
    /// </summary>
    private static Workflow Build(Records.WorkflowDefinition definition)
    {
        // Each step is a port where the run stops and waits for the outcome of the call that the agent makes there.
        Dictionary<string, RequestPort<Records.StepPrompt, Records.StepOutcome>> ports = definition.Steps.ToDictionary(
            step => step.Name,
            step => RequestPort.Create<Records.StepPrompt, Records.StepOutcome>(PortPrefix + step.Name),
            StringComparer.Ordinal);

        // The run enters at the first step with nothing bound yet.
        string firstStep = definition.Steps[0].Name;
        ExecutorBinding start = ((Func<string, IWorkflowContext, ValueTask<Records.StepPrompt>>)((_, _) =>
            new ValueTask<Records.StepPrompt>(new Records.StepPrompt(firstStep, new Dictionary<string, string>())))).BindAsExecutor("start");

        // The start leads only to the first step, which is where every launch stops for the first time.
        WorkflowBuilder builder = new WorkflowBuilder(start);
        builder.AddEdge<Records.StepPrompt>(start, ports[firstStep], prompt => prompt!.Step == firstStep);

        foreach (Records.WorkflowStep step in definition.Steps)
        {
            // Behind every port sits a router that turns the outcome into the prompt of the next step, or into nothing.
            ExecutorBinding router = ((Func<Records.StepOutcome, IWorkflowContext, ValueTask<Records.StepPrompt?>>)((outcome, _) =>
                new ValueTask<Records.StepPrompt?>(Route(definition, step, outcome)))).BindAsExecutor(RouterPrefix + step.Name);

            // The outcome that the agent hands to a port always reaches that step's own router.
            builder.AddEdge(ports[step.Name], router);

            // One graph edge per reachable step: several declared edges may lead to the same target on different tools,
            // and the router's prompt names the target that the condition lets through.
            foreach (string target in definition.Edges.Where(edge => edge.Source == step.Name).Select(edge => edge.Target).Distinct(StringComparer.Ordinal))
                builder.AddEdge<Records.StepPrompt>(router, ports[target], prompt => prompt!.Step == target);
        }

        // The definition never changes, so the graph of every call is the one that wrote the checkpoint it resumes.
        return builder.Build();
    }

    /// <summary>
    /// Picks the edge that the outcome follows and binds the values that it carries.
    /// </summary>
    /// <returns>The prompt for the next step; <c>null</c> when no edge leaves the step for this outcome and the workflow ends.</returns>
    private static Records.StepPrompt? Route(
        Records.WorkflowDefinition definition,
        Records.WorkflowStep step,
        Records.StepOutcome outcome)
    {
        // Startup refuses a transition declared twice, so the first match is the only one.
        Records.WorkflowEdge? edge = definition.Edges.FirstOrDefault(candidate =>
            string.Equals(candidate.Source, step.Name, StringComparison.Ordinal)
            && string.Equals(candidate.Tool, outcome.Tool, StringComparison.Ordinal)
            && candidate.OnFailure == outcome.Failed);

        // A call with no edge for its outcome ends the workflow: that is how a workflow declares its exits.
        if (edge is null)
            return null;

        // The edge names the properties that it carries from this result into the next step.
        Dictionary<string, string> arguments = [];
        foreach (string name in edge.Carrying)
        {
            // A result that lacks the field leaves the parameter unbound: the model supplies it.
            if (outcome.FieldsJson is not null && ReadField(outcome.FieldsJson, name) is { } value)
                arguments[name] = value;
        }

        // The prompt names its target, which is what lets only the graph edge to that step through.
        return new Records.StepPrompt(edge.Target, arguments);
    }

    /// <summary>
    /// Collects what a run produced since it was last looked at: the request waiting for the agent and the latest checkpoint.
    /// </summary>
    private static (ExternalRequest? Pending, CheckpointInfo? Checkpoint) Observe(Run run)
    {
        // A run that raised no request has ended: no step is waiting for the agent.
        ExternalRequest? pending = null;

        // The last request raised is where the run stopped; a failure anywhere in the run fails the turn that drove it.
        foreach (WorkflowEvent workflowEvent in run.NewEvents)
        {
            pending = workflowEvent switch
            {
                RequestInfoEvent requestEvent => requestEvent.Request,
                WorkflowErrorEvent errorEvent => throw new InvalidOperationException("The workflow engine failed", errorEvent.Exception),
                ExecutorFailedEvent failedEvent => throw new InvalidOperationException($"A workflow step failed: {failedEvent.Data}"),
                _ => pending
            };
        }

        // The last checkpoint is taken where the run stopped, which is where the next call resumes.
        return (pending, run.LastCheckpoint);
    }

    /// <summary>The step that a request waits at, read from the id of the port that raised it.</summary>
    private static string StepOf(ExternalRequest request)
        => request.PortInfo.PortId[PortPrefix.Length..];

    /// <summary>Turns the request now waiting for the agent into the position that the session keeps.</summary>
    private static Records.WorkflowPosition ToPosition(string workflow, ExternalRequest request, string checkpoint)
    {
        // Every port of this engine is raised by a step prompt, so a request carrying anything else is a broken graph.
        if (!request.TryGetDataAs(out Records.StepPrompt? prompt) || prompt is null)
            throw new InvalidOperationException($"Workflow '{workflow}' stopped at '{StepOf(request)}' without the prompt of its step");

        // The arguments are the values that the edge carried in, which the agent's tool calls at this step receive.
        return new Records.WorkflowPosition(workflow, StepOf(request), prompt.Arguments, checkpoint);
    }

    /// <summary>
    /// A checkpoint store that holds what a single call produces and hands the latest one out as a text, which
    /// is what the position keeps between calls.
    /// </summary>
    private sealed class LatestCheckpointStore : JsonCheckpointStore
    {
        /// <summary>The checkpoints written or imported, by id.</summary>
        private readonly Dictionary<string, JsonElement> items = [];

        /// <inheritdoc/>
        public override ValueTask<CheckpointInfo> CreateCheckpointAsync(string sessionId, JsonElement value, CheckpointInfo? parent = null)
        {
            // Every checkpoint that a call writes is kept: the position names only the latest of them.
            CheckpointInfo info = new CheckpointInfo(sessionId, Guid.NewGuid().ToString("N"));

            // The engine hands over an element of a document it disposes, so the store keeps a copy of its own.
            items[info.CheckpointId] = value.Clone();

            return new ValueTask<CheckpointInfo>(info);
        }

        /// <inheritdoc/>
        public override ValueTask<JsonElement> RetrieveCheckpointAsync(string sessionId, CheckpointInfo key)
            => new ValueTask<JsonElement>(items[key.CheckpointId]);

        /// <inheritdoc/>
        /// <remarks>The store lives for one call and holds no lineage, so the parent filter has nothing to narrow.</remarks>
        public override ValueTask<IEnumerable<CheckpointInfo>> RetrieveIndexAsync(string sessionId, CheckpointInfo? withParent = null)
            => new ValueTask<IEnumerable<CheckpointInfo>>(items.Keys.Select(id => new CheckpointInfo(sessionId, id)));

        /// <summary>Writes the given checkpoint, with the session it belongs to, as the text a position keeps.</summary>
        public string Export(CheckpointInfo latest)
            => JsonSerializer.Serialize(new CheckpointBag(latest.SessionId, latest.CheckpointId, items[latest.CheckpointId]));

        /// <summary>Rebuilds a store holding only the checkpoint that a bag carries, with the name the run resumes from.</summary>
        public static (LatestCheckpointStore Store, CheckpointInfo Latest) Import(string bag)
        {
            // A position's checkpoint is only ever written by Export, so it always holds one.
            CheckpointBag restored = JsonSerializer.Deserialize<CheckpointBag>(bag)!;

            // The resumed run reads its state from the one checkpoint that the session kept.
            LatestCheckpointStore store = new LatestCheckpointStore
            {
                items = { [restored.CheckpointId] = restored.Value }
            };

            // The run resumes in the session that wrote the checkpoint, under the same id.
            return (store, new CheckpointInfo(restored.SessionId, restored.CheckpointId));
        }
    }

    /// <summary>The text form of the latest checkpoint: the session and id that name it and its content.</summary>
    private sealed record CheckpointBag(string SessionId, string CheckpointId, JsonElement Value);
}
