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
        this.logger = logger;
        this.sharedVariableNames = [.. sharedVariableNames ?? []];
        Workflows = workflows ?? [];

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

        if (contextState.Variables.TryGetValue(variableName, out object? value))
        {
            logger.LogInformation("{MorganaAiContextProviderName} GET '{VariableName}' = '{Value}'", nameof(MorganaAIContextProvider), variableName, value);
            return value;
        }

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
        MorganaContextState contextState = sessionState.GetOrInitializeState(session);
        contextState.Variables[variableName] = variableValue;
        sessionState.SaveState(session, contextState);

        bool isShared = sharedVariableNames.Contains(variableName);

        logger.LogInformation(
            "{MorganaAiContextProviderName} SET {Private} '{VariableName}' = '{VariableValue}'", nameof(MorganaAIContextProvider), isShared ? "SHARED" : "PRIVATE", variableName, variableValue);

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

        if (contextState.Variables.Remove(variableName))
        {
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
        if (!sessionState.GetOrInitializeState(session).Variables.TryGetValue(Constants.ContextKeys.WorkflowPosition, out object? stored))
            return null;

        // Written this process lifetime it is the string stored below; restored from a saved session it is JSON.
        string? positionJson = stored switch
        {
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            _ => null
        };

        return string.IsNullOrEmpty(positionJson)
            ? null
            : JsonSerializer.Deserialize<Records.WorkflowPosition>(positionJson, Records.DefaultJsonSerializerOptions);
    }

    /// <summary>
    /// Stores where the running workflow stands, so that it is persisted and encrypted with the agent's row.
    /// </summary>
    public void SetWorkflowPosition(AgentSession session, Records.WorkflowPosition position)
    {
        MorganaContextState contextState = sessionState.GetOrInitializeState(session);
        contextState.Variables[Constants.ContextKeys.WorkflowPosition] = JsonSerializer.Serialize(position, Records.DefaultJsonSerializerOptions);
        sessionState.SaveState(session, contextState);

        logger.LogInformation(
            "{MorganaAiContextProviderName} workflow '{Workflow}' stands at step '{Step}'", nameof(MorganaAIContextProvider), position.Workflow, position.Step);
    }

    /// <summary>
    /// Forgets the running workflow, which ended or was abandoned.
    /// </summary>
    public void DropWorkflowPosition(AgentSession session)
        => DropVariable(session, Constants.ContextKeys.WorkflowPosition);

    /// <summary>
    /// Merges shared context variables received from a sibling agent.
    /// Applies first-write-wins: variables already present in local context are not overwritten.
    /// </summary>
    public void MergeSharedContext(AgentSession session, Dictionary<string, object> sharedContext)
    {
        MorganaContextState contextState = sessionState.GetOrInitializeState(session);
        bool changed = false;

        foreach (KeyValuePair<string, object> kvp in sharedContext)
        {
            if (!contextState.Variables.TryGetValue(kvp.Key, out object? existing))
            {
                contextState.Variables[kvp.Key] = kvp.Value;
                changed = true;

                logger.LogInformation(
                    "{MorganaAiContextProviderName} MERGED shared context '{KvpKey}' = '{KvpValue}'", nameof(MorganaAIContextProvider), kvp.Key, kvp.Value);
            }
            else
            {
                logger.LogInformation(
                    "{MorganaAiContextProviderName} IGNORED shared context '{KvpKey}' (already set to '{Existing}')", nameof(MorganaAIContextProvider), kvp.Key, existing);
            }
        }

        if (changed)
            sessionState.SaveState(session, contextState);
    }

    // =========================================================================
    // AIContextProvider overrides
    // =========================================================================

    /// <summary>
    /// Called AFTER each agent invocation. Override to inspect response messages and apply context updates.
    /// </summary>
    protected override ValueTask StoreAIContextAsync(
        InvokedContext context,
        CancellationToken cancellationToken = default)
    {
        // Reserved for future use: extract state from response messages and persist via sessionState.SaveState.
        return ValueTask.CompletedTask;
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