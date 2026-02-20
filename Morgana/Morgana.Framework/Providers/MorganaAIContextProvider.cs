using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;

namespace Morgana.Framework.Providers;

/// <summary>
/// Manages per-session conversation context variables for a Morgana agent.
/// Supports cross-agent variable sharing via the RouterActor broadcast mechanism.
/// </summary>
/// <remarks>
/// <para>One instance is created per agent intent and shared across all sessions of that agent.
/// Session-specific state (the variable dictionary) lives in <see cref="AgentSession"/> via
/// <see cref="ProviderSessionState{T}"/> and is serialized automatically by the framework.</para>
///
/// <para><strong>Key behaviours:</strong></para>
/// <list type="bullet">
/// <item><term>Variable storage</term><description>Per-session dictionary persisted inside AgentSession.</description></item>
/// <item><term>Shared variables</term><description>Variables declared as shared are broadcast to sibling agents via <see cref="OnSharedContextUpdate"/>.</description></item>
/// <item><term>Merge strategy</term><description>First-write-wins: incoming shared values are ignored if the variable is already set locally.</description></item>
/// </list>
///
/// <para><strong>Integration overview:</strong></para>
/// <code>
/// MorganaAgent (one per intent)
///   └── MorganaAIContextProvider (singleton, attached to AIAgent)
///         ├── ProviderSessionState&lt;MorganaContextState&gt; → AgentSession (per-session)
///         ├── SharedVariableNames (derived from tool definitions at startup)
///         └── OnSharedContextUpdate → RouterActor broadcast
///
/// MorganaTool
///   ├── GetContextVariable → MorganaAIContextProvider.GetVariable(session, name)
///   └── SetContextVariable → MorganaAIContextProvider.SetVariable(session, name, value)
///                                └── triggers broadcast if variable is shared
/// </code>
/// </remarks>
public class MorganaAIContextProvider : AIContextProvider
{
    private readonly ILogger logger;

    /// <summary>
    /// Names of variables subject to automatic cross-agent broadcasting.
    /// Derived from tool definitions (Scope="context", Shared=true) at construction time.
    /// </summary>
    private readonly ImmutableHashSet<string> sharedVariableNames;

    /// <summary>
    /// Manages storage and retrieval of <see cref="MorganaContextState"/> within <see cref="AgentSession"/>.
    /// </summary>
    private readonly ProviderSessionState<MorganaContextState> sessionState;

    /// <summary>
    /// Invoked when a shared variable is written.
    /// Wired by MorganaAgent to broadcast the update to the RouterActor.
    /// </summary>
    public Action<string, object>? OnSharedContextUpdate { get; set; }

    /// <summary>
    /// Key used by the framework to store and retrieve this provider's state within <see cref="AgentSession"/>.
    /// </summary>
    public override string StateKey => nameof(MorganaAIContextProvider);

    /// <summary>
    /// Initializes a new singleton instance of <see cref="MorganaAIContextProvider"/>.
    /// </summary>
    /// <param name="logger">Logger for context operation diagnostics.</param>
    /// <param name="sharedVariableNames">
    /// Names of variables that should be broadcast to other agents when set.
    /// Typically extracted from tool definitions where Scope="context" and Shared=true.
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
            stateKey: StateKey,
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
            logger.LogInformation($"{nameof(MorganaAIContextProvider)} GET '{variableName}' = '{value}'");
            return value;
        }

        logger.LogInformation($"{nameof(MorganaAIContextProvider)} MISS '{variableName}'");
        return null;
    }

    /// <summary>
    /// Writes a variable to the session's conversation context.
    /// If the variable is declared as shared, <see cref="OnSharedContextUpdate"/> is invoked
    /// to broadcast the new value to sibling agents via the RouterActor.
    /// </summary>
    public void SetVariable(AgentSession session, string variableName, object variableValue)
    {
        MorganaContextState contextState = sessionState.GetOrInitializeState(session);
        contextState.Variables[variableName] = variableValue;
        sessionState.SaveState(session, contextState);

        bool isShared = sharedVariableNames.Contains(variableName);

        logger.LogInformation(
            $"{nameof(MorganaAIContextProvider)} SET {(isShared ? "SHARED" : "PRIVATE")} '{variableName}' = '{variableValue}'");

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
            logger.LogInformation($"{nameof(MorganaAIContextProvider)} DROPPED '{variableName}'");
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
                    $"{nameof(MorganaAIContextProvider)} MERGED shared context '{kvp.Key}' = '{kvp.Value}'");
            }
            else
            {
                logger.LogInformation(
                    $"{nameof(MorganaAIContextProvider)} IGNORED shared context '{kvp.Key}' (already set to '{existing}')");
            }
        }

        if (changed)
            sessionState.SaveState(session, contextState);
    }

    /// <summary>
    /// Re-broadcasts all shared variables for a session via <see cref="OnSharedContextUpdate"/>.
    /// Called after a session is loaded so that sibling agents receive any shared values
    /// that were established during a previous conversation turn.
    /// </summary>
    public void PropagateSharedVariables(AgentSession session)
    {
        MorganaContextState contextState = sessionState.GetOrInitializeState(session);
        int propagatedCount = 0;

        foreach (string sharedVariableName in sharedVariableNames)
        {
            if (contextState.Variables.TryGetValue(sharedVariableName, out object? value))
            {
                OnSharedContextUpdate?.Invoke(sharedVariableName, value);
                propagatedCount++;

                logger.LogInformation(
                    $"{nameof(MorganaAIContextProvider)} PROPAGATED shared variable '{sharedVariableName}' = '{value}'");
            }
        }

        if (propagatedCount > 0)
        {
            logger.LogInformation(
                $"{nameof(MorganaAIContextProvider)} PROPAGATED {propagatedCount} shared variables to other agents");
        }
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