using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;
using Morgana.AI.Interfaces;

namespace Morgana.AI.Providers;

/// <summary>
/// Per-agent singleton managing session-level conversation variables. Shared variables trigger OnSharedContextUpdate
/// callback for cross-agent persistence in conversation-scoped shared_context registry (first-write-wins merge).
/// Storage: ProviderSessionState&lt;MorganaContextState&gt; → AgentSession (auto-serialized by framework).
/// </summary>
public class MorganaAIContextProvider : AIContextProvider
{
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
    /// Assembles the per-turn declaration naming the variables this session holds. Returns
    /// <c>null</c> when nothing should be injected — no variables held, or no template declared by
    /// the prompt layer — in which case behaviour is unchanged.
    /// </summary>
    private readonly IPromptComposerService? promptComposerService;

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
    /// <param name="promptComposerService">
    /// Composes the per-turn held-context declaration. Left null,
    /// <see cref="ProvideAIContextAsync"/> injects nothing.
    /// </param>
    public MorganaAIContextProvider(
        ILogger logger,
        IEnumerable<string>? sharedVariableNames = null,
        JsonSerializerOptions? jsonSerializerOptions = null,
        IPromptComposerService? promptComposerService = null)
    {
        this.logger = logger;
        this.sharedVariableNames = [.. sharedVariableNames ?? []];
        this.promptComposerService = promptComposerService;

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
    /// Per-turn injection: hands the model the context variables the session holds, name and value,
    /// ordinal-sorted. Critical for agents activated mid-conversation on empty per-agent history:
    /// hydrated shared variables from registry are invisible in history. Empty session → no injection.
    /// </summary>
    protected override async ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        if (promptComposerService is null)
            return new AIContext();

        MorganaContextState contextState = sessionState.GetOrInitializeState(context.Session);

        // Strip the framework's own ephemeral keys (turn_continuation, quick_replies, rich_card) —
        // they are not inputs to resolve, never something the model should be told it "holds". The
        // SortedDictionary keeps keys in ordinal order so the declaration text is byte-identical
        // across turns whenever the held set itself doesn't change (keeps the composed prompt stable).
        SortedDictionary<string, object> heldVariables = new SortedDictionary<string, object>(
            contextState.Variables
                .Where(kvp => !EphemeralVariableNames.Contains(kvp.Key))
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
            StringComparer.Ordinal);

        string? declaration = await promptComposerService.ComposeHeldContextDeclarationAsync(heldVariables);

        if (declaration is null)
            return new AIContext();

        logger.LogInformation(
            "{MorganaAiContextProviderName} DECLARED '{VariableNames}'",
            nameof(MorganaAIContextProvider), string.Join(", ", heldVariables.Keys));

        return new AIContext { Instructions = declaration };
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
        /// <summary>Conversation variables for this session (e.g. customerCode, invoiceId).</summary>
        [JsonPropertyName("variables")]
        public Dictionary<string, object> Variables { get; set; } = [];
    }
}