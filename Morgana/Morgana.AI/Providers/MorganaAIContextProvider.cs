using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;

namespace Morgana.AI.Providers;

/// <summary>
/// Per-agent singleton managing session-level conversation variables. Shared variables trigger OnSharedContextUpdate
/// callback for cross-agent persistence in conversation-scoped shared_context registry (first-write-wins merge).
/// Storage: ProviderSessionState&lt;MorganaContextState&gt; → AgentSession (auto-serialized by framework).
/// </summary>
public class MorganaAIContextProvider : AIContextProvider
{
    /// <summary>Logger for provider-level diagnostics.</summary>
    private readonly ILogger logger;

    /// <summary>
    /// Names of variables subject to cross-agent persistence in the conversation-scoped
    /// <c>shared_context</c> registry. Derived from tool definitions (Scope="context",
    /// Shared=true) at construction time.
    /// </summary>
    private readonly ImmutableHashSet<string> sharedVariableNames;

    /// <summary>
    /// Manages storage and retrieval of <see cref="MorganaContextState"/> within <see cref="AgentSession"/>.
    /// </summary>
    private readonly ProviderSessionState<MorganaContextState> sessionState;

    /// <summary>
    /// Invoked when a shared variable is written. Wired by MorganaAgent to persist the value
    /// into the conversation-scoped <c>shared_context</c> registry, where every agent of the
    /// conversation can hydrate it at the start of its next turn. Awaited by the writing tool
    /// call, so the persisted write completes before the turn issues its next tool call.
    /// </summary>
    public Func<string, object, Task>? OnSharedContextUpdate { get; set; }

    /// <summary>
    /// The workflows of the agent that owns this provider, so that whoever holds the provider resolves a
    /// stored position without reaching the tool registry.
    /// </summary>
    public IReadOnlyList<Records.WorkflowDefinition> Workflows { get; }

    /// <summary>
    /// Keys used by the framework to store and retrieve this provider's state within <see cref="AgentSession"/>.
    /// </summary>
    public override IReadOnlyList<string> StateKeys => [ nameof(MorganaAIContextProvider) ];

    /// <summary>
    /// Initializes a new singleton instance of <see cref="MorganaAIContextProvider"/>.
    /// </summary>
    /// <param name="logger">Logger for context operation diagnostics.</param>
    /// <param name="sharedVariableNames">
    /// Names of variables that should be persisted into the conversation-scoped
    /// <c>shared_context</c> registry when set. Typically extracted from tool definitions where
    /// Scope="context" and Shared=true.
    /// </param>
    /// <param name="jsonSerializerOptions">
    /// JSON serialization options for state persistence.
    /// Defaults to <c>AgentAbstractionsJsonUtilities.DefaultOptions</c>.
    /// </param>
    /// <param name="workflows">The workflows that the agent runs; none for an agent that declares no workflow.</param>
    public MorganaAIContextProvider(
        ILogger logger,
        IEnumerable<string>? sharedVariableNames = null,
        JsonSerializerOptions? jsonSerializerOptions = null,
        IReadOnlyList<Records.WorkflowDefinition>? workflows = null)
    {
        // A provider built without shared names persists nothing across agents.
        this.logger = logger;
        this.sharedVariableNames = [.. sharedVariableNames ?? []];
        Workflows = workflows ?? [];

        // A session that holds no variables yet starts with an empty set, so the first turn needs no special case.
        sessionState = new ProviderSessionState<MorganaContextState>(
            stateInitializer: _ => new MorganaContextState(),
            stateKey: StateKeys[0],
            jsonSerializerOptions: jsonSerializerOptions ?? AgentAbstractionsJsonUtilities.DefaultOptions);
    }

    // =========================================================================
    // Agent Context
    // =========================================================================

    /// <summary>
    /// Retrieves a variable from the session's conversation context.
    /// Returns <c>null</c> if the variable has not been set.
    /// </summary>
    public object? GetVariable(AgentSession session, string variableName)
    {
        MorganaContextState contextState = sessionState.GetOrInitializeState(session);

        // A hit logs the value the tool is about to use.
        if (contextState.Variables.TryGetValue(variableName, out object? value))
        {
            logger.LogInformation("{MorganaAiContextProviderName} GET '{VariableName}' = '{Value}'", nameof(MorganaAIContextProvider), variableName, value);
            return value;
        }

        // A miss is reported as null: the caller treats it as a value that nobody has given yet.
        logger.LogInformation("{MorganaAiContextProviderName} MISS '{VariableName}'", nameof(MorganaAIContextProvider), variableName);
        return null;
    }

    /// <summary>
    /// Writes a variable to the session's conversation context.
    /// If the variable is declared as shared, <see cref="OnSharedContextUpdate"/> is invoked
    /// to persist the value into the conversation-scoped <c>shared_context</c> registry where
    /// other agents can hydrate it on their next turn.
    /// </summary>
    public async Task SetVariableAsync(AgentSession session, string variableName, object variableValue)
    {
        // The value always lands in this agent's own session: a later write replaces an earlier one here.
        MorganaContextState contextState = sessionState.GetOrInitializeState(session);
        contextState.Variables[variableName] = variableValue;
        sessionState.SaveState(session, contextState);

        // Only the variables that tools declared shared travel on to the other agents.
        bool isShared = sharedVariableNames.Contains(variableName);

        logger.LogInformation(
            "{MorganaAiContextProviderName} SET {Private} '{VariableName}' = '{VariableValue}'", nameof(MorganaAIContextProvider), isShared ? "SHARED" : "PRIVATE", variableName, variableValue);

        // The registry write is awaited so that it is complete before the turn issues its next tool call.
        if (isShared && OnSharedContextUpdate is not null)
            await OnSharedContextUpdate(variableName, variableValue);
    }

    /// <summary>
    /// Removes a variable from the session's conversation context.
    /// Used to discard what lasts one turn, such as the turn's closure, once it has been read.
    /// </summary>
    public void DropVariable(AgentSession session, string variableName)
    {
        MorganaContextState contextState = sessionState.GetOrInitializeState(session);

        // The session is saved only when something was dropped: an absent variable leaves it as it is.
        if (contextState.Variables.Remove(variableName))
        {
            // The changed state is saved, so the dropped variable stays dropped in the session.
            sessionState.SaveState(session, contextState);
            logger.LogInformation("{MorganaAiContextProviderName} DROPPED '{VariableName}'", nameof(MorganaAIContextProvider), variableName);
        }
    }

    /// <summary>
    /// Reads where the running workflow stands; <c>null</c> when none runs.
    /// </summary>
    /// <remarks>
    /// Read without the per-variable access line: the position carries the engine's whole checkpoint and
    /// the model-call filter reads it before every call.
    /// </remarks>
    public Records.WorkflowPosition? GetWorkflowPosition(AgentSession session)
    {
        // No stored position means that no workflow runs for this agent.
        if (!sessionState.GetOrInitializeState(session).Variables.TryGetValue(Constants.ContextKeys.WorkflowPosition, out object? storedPosition))
            return null;

        // Written this process lifetime it is the string stored below; restored from a saved session it is JSON.
        string? positionJson = storedPosition switch
        {
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            _ => null
        };

        // An empty or unreadable value is the same as no position.
        return string.IsNullOrEmpty(positionJson)
            ? null
            : JsonSerializer.Deserialize<Records.WorkflowPosition>(positionJson, Records.DefaultJsonSerializerOptions);
    }

    /// <summary>
    /// Stores where the running workflow stands, so that it is persisted and encrypted with the agent's row.
    /// </summary>
    public void SetWorkflowPosition(AgentSession session, Records.WorkflowPosition position)
    {
        // The position is stored as JSON text in the variables, so that it is persisted and encrypted with the rest of the session.
        MorganaContextState contextState = sessionState.GetOrInitializeState(session);
        contextState.Variables[Constants.ContextKeys.WorkflowPosition] = JsonSerializer.Serialize(position, Records.DefaultJsonSerializerOptions);
        sessionState.SaveState(session, contextState);

        // The log line traces the workflow's progress step by step.
        logger.LogInformation(
            "{MorganaAiContextProviderName} workflow '{Workflow}' stands at step '{Step}'", nameof(MorganaAIContextProvider), position.Workflow, position.Step);
    }

    /// <summary>
    /// Forgets the running workflow, which ended or was abandoned.
    /// </summary>
    public void DropWorkflowPosition(AgentSession session)
        => DropVariable(session, Constants.ContextKeys.WorkflowPosition);

    /// <summary>
    /// Reads how many consultations the current turn has spent; zero when it has spent none.
    /// </summary>
    /// <remarks>
    /// Read without the per-variable access line: the model-call filter reads it before every call.
    /// </remarks>
    public int GetConsultationRounds(AgentSession session)
        => sessionState.GetOrInitializeState(session).Variables.TryGetValue(Constants.ContextKeys.ConsultationRounds, out object? storedRounds)
            // Counted in this process the rounds are a number; restored from a saved session they are JSON.
            ? storedRounds switch
            {
                int rounds => rounds,
                JsonElement { ValueKind: JsonValueKind.Number } element => element.GetInt32(),
                _ => 0
            }
            : 0;

    /// <summary>
    /// Merges shared context variables received from a sibling agent.
    /// Applies first-write-wins: variables already present in local context are not overwritten.
    /// </summary>
    public void MergeSharedContext(AgentSession session, Dictionary<string, object> sharedContext)
    {
        // The agent's context is read before the shared values are merged into it.
        MorganaContextState contextState = sessionState.GetOrInitializeState(session);
        bool changed = false;

        // Each shared value is merged in turn, under the first-write-wins rule.
        foreach (KeyValuePair<string, object> kvp in sharedContext)
        {
            // First write wins: a value this agent already holds is the one the user gave it and is never replaced.
            if (!contextState.Variables.TryGetValue(kvp.Key, out object? heldValue))
            {
                // A value the agent does not hold yet is taken from the registry.
                contextState.Variables[kvp.Key] = kvp.Value;
                changed = true;

                logger.LogInformation(
                    "{MorganaAiContextProviderName} MERGED shared context '{KvpKey}' = '{KvpValue}'", nameof(MorganaAIContextProvider), kvp.Key, kvp.Value);
            }
            else
            {
                logger.LogInformation(
                    "{MorganaAiContextProviderName} IGNORED shared context '{KvpKey}' (already set to '{Existing}')", nameof(MorganaAIContextProvider), kvp.Key, heldValue);
            }
        }

        // The session is saved only when a value was adopted.
        if (changed)
            sessionState.SaveState(session, contextState);
    }

    /// <summary>
    /// Per-session state stored inside <see cref="AgentSession"/> via <see cref="ProviderSessionState{T}"/>.
    /// Serialized and restored automatically by the framework as part of session persistence.
    /// </summary>
    public sealed class MorganaContextState
    {
        /// <summary>Conversation variables for this session (e.g. customerCode, invoiceId).</summary>
        [JsonPropertyName("variables")]
        public Dictionary<string, object> Variables { get; set; } = [];
    }
}