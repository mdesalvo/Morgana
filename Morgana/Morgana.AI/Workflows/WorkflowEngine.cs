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
    /// <summary>The scope that keeps the result of every step, so that a bound value may come from any earlier step.</summary>
    private const string ResultScope = "workflow-results";

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
        Records.WorkflowDefinition definition = definitions[workflowName];
        LatestCheckpointStore store = new LatestCheckpointStore();

        await using Run run = await InProcessExecution.RunAsync(
            Build(definition), workflowName, CheckpointManager.CreateJson(store), Guid.NewGuid().ToString("N"));

        ExternalRequest request = Observe(run).Pending
            ?? throw new InvalidOperationException($"Workflow '{workflowName}' ended without reaching its first step");

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
        Records.WorkflowDefinition definition = definitions[position.Workflow];
        LatestCheckpointStore store = LatestCheckpointStore.Import(position.Checkpoint, out CheckpointInfo checkpoint);

        await using Run run = await InProcessExecution.ResumeAsync(Build(definition), checkpoint, CheckpointManager.CreateJson(store));

        // The step is read from the port that the engine is waiting at: a position that disagrees is a stale session.
        ExternalRequest waiting = Observe(run).Pending
            ?? throw new InvalidOperationException($"Workflow '{position.Workflow}' has no step waiting for an outcome");
        if (!string.Equals(StepOf(waiting), position.Step, StringComparison.Ordinal))
            throw new InvalidOperationException($"Workflow '{position.Workflow}' waits at '{StepOf(waiting)}' while its position says '{position.Step}'");

        await run.ResumeAsync([waiting.CreateResponse(outcome)]);

        // No pending request after the outcome means the router sent nothing on: the workflow ended.
        (ExternalRequest? Pending, CheckpointInfo? Checkpoint) next = Observe(run);
        return next.Pending is null ? null : ToPosition(position.Workflow, next.Pending, store.Export(next.Checkpoint!));
    }

    /// <summary>
    /// Reads one field of a tool's result, however the record cased it.
    /// </summary>
    /// <param name="resultJson">The tool's result as the JSON text the model read.</param>
    /// <param name="field">The field as agents.json declares it.</param>
    /// <returns>The field's JSON text; <c>null</c> when the field is absent or null or the result is not an object.</returns>
    public static string? ReadField(string resultJson, string field)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(resultJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (string.Equals(property.Name, field, StringComparison.OrdinalIgnoreCase))
                    return property.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : property.Value.GetRawText();
            }

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
        Dictionary<string, RequestPort<Records.StepPrompt, Records.StepOutcome>> ports = definition.Steps.ToDictionary(
            step => step.Name,
            step => RequestPort.Create<Records.StepPrompt, Records.StepOutcome>(PortPrefix + step.Name),
            StringComparer.Ordinal);

        // A lambda executor may only send the types it declares, so each one is typed and returns its message.
        string firstStep = definition.Steps[0].Name;
        ExecutorBinding start = ((Func<string, IWorkflowContext, ValueTask<Records.StepPrompt>>)((_, _) =>
            new ValueTask<Records.StepPrompt>(new Records.StepPrompt(firstStep, new Dictionary<string, string>())))).BindAsExecutor("start");

        WorkflowBuilder builder = new WorkflowBuilder(start);
        builder.AddEdge<Records.StepPrompt>(start, ports[firstStep], prompt => prompt!.Step == firstStep);

        foreach (Records.WorkflowStep step in definition.Steps)
        {
            Records.WorkflowStep current = step;
            ExecutorBinding router = ((Func<Records.StepOutcome, IWorkflowContext, ValueTask<Records.StepPrompt?>>)(async (outcome, context) =>
                await RouteAsync(definition, current, outcome, context))).BindAsExecutor(RouterPrefix + step.Name);

            builder.AddEdge(ports[step.Name], router);

            foreach (string target in step.Links(false).Values.Concat(step.Links(true).Values).Distinct(StringComparer.Ordinal)
                         .Where(target => target != Constants.Workflows.End))
                builder.AddEdge<Records.StepPrompt>(router, ports[target], prompt => prompt!.Step == target);
        }

        return builder.Build();
    }

    /// <summary>
    /// Keeps the step's result and picks the step that follows the outcome, binding its arguments.
    /// </summary>
    /// <returns>The prompt for the next step; <c>null</c> when the workflow ends.</returns>
    private static async ValueTask<Records.StepPrompt?> RouteAsync(
        Records.WorkflowDefinition definition,
        Records.WorkflowStep step,
        Records.StepOutcome outcome,
        IWorkflowContext context)
    {
        await context.QueueStateUpdateAsync(step.Name, outcome.ResultJson, ResultScope);

        // A tool that the step does not link on this outcome ends the workflow, which is how End is also spelled.
        if (!step.Links(outcome.Failed).TryGetValue(outcome.Tool, out string? target) || target == Constants.Workflows.End)
            return null;

        Records.WorkflowStep next = definition.Steps.First(candidate => string.Equals(candidate.Name, target, StringComparison.Ordinal));
        Dictionary<string, string> arguments = [];

        foreach ((string parameter, string source) in next.BoundArguments())
        {
            string[] parts = source.Split('.', 2);

            // The step just left has its result in hand: a queued update is not readable until the superstep ends.
            string? resultJson = parts[0] == step.Name
                ? outcome.ResultJson
                : await context.ReadStateAsync<string>(parts[0], ResultScope);

            // A source that never ran or lacks the field leaves the parameter unbound: the model supplies it.
            if (resultJson is not null && ReadField(resultJson, parts[1]) is { } value)
                arguments[parameter] = value;
        }

        return new Records.StepPrompt(target, arguments);
    }

    /// <summary>
    /// Collects what a run produced since it was last looked at: the request waiting for the agent and the latest checkpoint.
    /// </summary>
    private static (ExternalRequest? Pending, CheckpointInfo? Checkpoint) Observe(Run run)
    {
        ExternalRequest? pending = null;

        foreach (WorkflowEvent workflowEvent in run.NewEvents)
        {
            switch (workflowEvent)
            {
                case RequestInfoEvent requestEvent:
                    pending = requestEvent.Request;
                    break;
                case WorkflowErrorEvent errorEvent:
                    throw new InvalidOperationException("The workflow engine failed", errorEvent.Exception);
                case ExecutorFailedEvent failedEvent:
                    throw new InvalidOperationException($"A workflow step failed: {failedEvent.Data}");
            }
        }

        return (pending, run.LastCheckpoint);
    }

    /// <summary>The step that a request waits at, read from the id of the port that raised it.</summary>
    private static string StepOf(ExternalRequest request)
        => request.PortInfo.PortId[PortPrefix.Length..];

    /// <summary>Turns the request now waiting for the agent into the position that the session keeps.</summary>
    private static Records.WorkflowPosition ToPosition(string workflow, ExternalRequest request, string checkpoint)
    {
        request.TryGetDataAs(out Records.StepPrompt? prompt);

        return new Records.WorkflowPosition(workflow, StepOf(request), prompt?.Arguments ?? new Dictionary<string, string>(), checkpoint);
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
            CheckpointInfo info = new CheckpointInfo(sessionId, Guid.NewGuid().ToString("N"));
            items[info.CheckpointId] = value.Clone();

            return new ValueTask<CheckpointInfo>(info);
        }

        /// <inheritdoc/>
        public override ValueTask<JsonElement> RetrieveCheckpointAsync(string sessionId, CheckpointInfo key)
            => new ValueTask<JsonElement>(items[key.CheckpointId]);

        /// <inheritdoc/>
        public override ValueTask<IEnumerable<CheckpointInfo>> RetrieveIndexAsync(string sessionId, CheckpointInfo? withParent = null)
            => new ValueTask<IEnumerable<CheckpointInfo>>(items.Keys.Select(id => new CheckpointInfo(sessionId, id)));

        /// <summary>Writes the given checkpoint, with the session it belongs to, as the text a position keeps.</summary>
        public string Export(CheckpointInfo latest)
            => JsonSerializer.Serialize(new CheckpointBag(latest.SessionId, latest.CheckpointId, items[latest.CheckpointId]));

        /// <summary>Rebuilds a store holding only the checkpoint that a bag carries.</summary>
        public static LatestCheckpointStore Import(string bag, out CheckpointInfo latest)
        {
            CheckpointBag restored = JsonSerializer.Deserialize<CheckpointBag>(bag)!;
            latest = new CheckpointInfo(restored.SessionId, restored.CheckpointId);

            LatestCheckpointStore store = new LatestCheckpointStore();
            store.items[restored.CheckpointId] = restored.Value;

            return store;
        }
    }

    /// <summary>The text form of the latest checkpoint: the session and id that name it and its content.</summary>
    private sealed record CheckpointBag(string SessionId, string CheckpointId, JsonElement Value);
}
