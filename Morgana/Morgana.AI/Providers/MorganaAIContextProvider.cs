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
    /// <summary>
    /// Placeholder in the HeldContextDeclaration injection template, resolved per invocation to
    /// the comma-separated names of the variables this session currently holds.
    /// </summary>
    private const string HeldVariablesPlaceholder = "((held_variables))";

    /// <summary>
    /// Reserved keys the framework writes into the same dictionary to carry a turn's presentation
    /// decisions (see <c>MorganaTool.SetTurnContinuation/SetQuickReplies/SetRichCard</c>, drained by
    /// <c>MorganaAgent</c> at the end of every turn). They are never declared to the model: they are
    /// not inputs to resolve, and naming a stale one would invite the next turn to re-read buttons or
    /// a card that has already been rendered and consumed.
    /// </summary>
    private static readonly ImmutableHashSet<string> EphemeralVariableNames =
        ["turn_continuation", "quick_replies", "rich_card"];

    /// <summary>Logger for provider-level diagnostics.</summary>
    private readonly ILogger logger;

    /// <summary>
    /// The HeldContextDeclaration template from <c>morgana.json</c>, or <c>null</c> when the prompt
    /// layer declares none — in which case nothing is injected and behaviour is unchanged.
    /// </summary>
    private readonly string? heldContextDeclaration;

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
    /// conversation can hydrate it at the start of its next turn.
    /// </summary>
    public Action<string, object>? OnSharedContextUpdate { get; set; }

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
    /// <param name="heldContextDeclaration">
    /// The HeldContextDeclaration injection template resolved from <c>morgana.json</c>, carrying the
    /// <c>((held_variables))</c> placeholder. Left null, <see cref="ProvideAIContextAsync"/> injects
    /// nothing.
    /// </param>
    public MorganaAIContextProvider(
        ILogger logger,
        IEnumerable<string>? sharedVariableNames = null,
        JsonSerializerOptions? jsonSerializerOptions = null,
        string? heldContextDeclaration = null)
    {
        this.logger = logger;
        this.sharedVariableNames = [.. sharedVariableNames ?? []];
        this.heldContextDeclaration = string.IsNullOrWhiteSpace(heldContextDeclaration) ? null : heldContextDeclaration;

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
    public void SetVariable(AgentSession session, string variableName, object variableValue)
    {
        MorganaContextState contextState = sessionState.GetOrInitializeState(session);
        contextState.Variables[variableName] = variableValue;
        sessionState.SaveState(session, contextState);

        bool isShared = sharedVariableNames.Contains(variableName);

        logger.LogInformation(
            "{MorganaAiContextProviderName} SET {Private} '{VariableName}' = '{VariableValue}'", nameof(MorganaAIContextProvider), isShared ? "SHARED" : "PRIVATE", variableName, variableValue);

        if (isShared)
            OnSharedContextUpdate?.Invoke(variableName, variableValue);
    }

    /// <summary>
    /// Removes a variable from the session's conversation context.
    /// Used to discard ephemeral data (e.g. quick replies, rich cards) after they have been consumed.
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
    /// Per-turn injection: declares by name only which context variables session holds (names only, never values,
    /// silent when empty, ordinal-sorted for cache stability). Critical for agents activated mid-conversation
    /// on empty per-agent history: hydrated shared variables from registry are invisible in history.
    /// </summary>
    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        if (heldContextDeclaration is null)
            return ValueTask.FromResult(new AIContext());

        MorganaContextState contextState = sessionState.GetOrInitializeState(context.Session);

        string[] heldVariables = [.. contextState.Variables.Keys
            .Where(name => !EphemeralVariableNames.Contains(name))
            .Order(StringComparer.Ordinal)];

        if (heldVariables.Length == 0)
            return ValueTask.FromResult(new AIContext());

        string names = string.Join(", ", heldVariables);

        logger.LogInformation(
            "{MorganaAiContextProviderName} DECLARED '{VariableNames}'", nameof(MorganaAIContextProvider), names);

        return ValueTask.FromResult(new AIContext
        {
            Instructions = heldContextDeclaration.Replace(HeldVariablesPlaceholder, names)
        });
    }

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
        /// <summary>Conversation variables for this session (e.g. userId, invoiceId).</summary>
        [JsonPropertyName("variables")]
        public Dictionary<string, object> Variables { get; set; } = [];
    }
}