using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;

namespace Morgana.AI.Providers;

/// <summary>
/// Manages per-session conversation context variables for a Morgana agent.
/// Cross-agent variable sharing is delegated to the conversation-scoped <c>shared_context</c>
/// registry on the persistence layer (see <see cref="OnSharedContextUpdate"/>).
/// </summary>
/// <remarks>
/// <para>One instance is created per agent intent and shared across all sessions of that agent.
/// Session-specific state (the variable dictionary) lives in <see cref="AgentSession"/> via
/// <see cref="ProviderSessionState{T}"/> and is serialized automatically by the framework.</para>
///
/// <para><strong>Key behaviours:</strong></para>
/// <list type="bullet">
/// <item><term>Variable storage</term><description>Per-session dictionary persisted inside AgentSession.</description></item>
/// <item><term>Shared variables</term><description>Variables declared as shared raise the <see cref="OnSharedContextUpdate"/>
///     callback, which MorganaAgent wires to a write into the conversation-scoped <c>shared_context</c>
///     registry on the persistence layer.</description></item>
/// <item><term>Merge strategy</term><description>First-write-wins: incoming shared values are ignored if the variable is already set locally.</description></item>
/// </list>
///
/// <para><strong>Integration overview:</strong></para>
/// <code>
/// MorganaAgent (one per intent)
///   └── MorganaAIContextProvider (singleton, attached to AIAgent)
///         ├── ProviderSessionState&lt;MorganaContextState&gt; → AgentSession (per-session)
///         ├── SharedVariableNames (derived from tool definitions at startup)
///         └── OnSharedContextUpdate → IConversationPersistenceService.UpsertSharedVariableAsync
///                                      (writes into per-conversation shared_context table)
///
/// MorganaTool
///   ├── GetContextVariable → MorganaAIContextProvider.GetVariable(session, name)
///   └── SetContextVariable → MorganaAIContextProvider.SetVariable(session, name, value)
///                                └── if Shared → fires OnSharedContextUpdate → DB write
/// </code>
/// </remarks>
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
    public MorganaAIContextProvider(
        ILogger logger,
        IEnumerable<string>? sharedVariableNames = null,
        JsonSerializerOptions? jsonSerializerOptions = null)
    {
        this.logger = logger;
        this.sharedVariableNames = [.. sharedVariableNames ?? []];

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
    /// Called BEFORE each agent invocation.
    /// </summary>
    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        // Reserved for future use: inject transient system prompt additions based on current context state.
        return ValueTask.FromResult(new AIContext());
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