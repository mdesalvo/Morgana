using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;

namespace Morgana.Framework.Providers;

/// <summary>
/// Morgana's extension of AIContextProvider that maintains agent conversation context variables.
/// Supports automatic serialization/deserialization for persistence with AgentThread
/// and cross-agent context sharing via RouterActor broadcast mechanism.
/// </summary>
/// <remarks>
/// <para><strong>Purpose:</strong></para>
/// <para>This provider serves as the central storage for conversation variables (e.g., userId, invoiceId)
/// that agents need to remember across multiple turns. It integrates with the Microsoft.Agents.AI framework
/// while adding Morgana-specific features like shared variable broadcasting.</para>
/// <para><strong>Key Features:</strong></para>
/// <list type="bullet">
/// <item><term>Variable Storage</term><description>Dictionary-based storage for conversation variables</description></item>
/// <item><term>Shared Variables</term><description>Automatic broadcast of shared variables to other agents</description></item>
/// <item><term>First-Write-Wins</term><description>Conflict resolution for cross-agent variable merging</description></item>
/// <item><term>Serialization</term><description>JSON serialization for AgentThread persistence</description></item>
/// <item><term>Logging</term><description>Comprehensive logging of all variable operations</description></item>
/// </list>
/// <para><strong>Architecture Integration:</strong></para>
/// <code>
/// MorganaAgent
///   └── MorganaContextProvider (one per agent instance)
///         ├── AgentContext Dictionary (variable storage)
///         ├── SharedVariableNames HashSet (from tool definitions)
///         └── OnSharedContextUpdate Callback (broadcasts to RouterActor)
///
/// MorganaTool
///   ├── GetContextVariable() → MorganaContextProvider.GetVariable()
///   └── SetContextVariable() → MorganaContextProvider.SetVariable()
///                                  └── Triggers broadcast if shared
/// </code>
/// <para><strong>Shared Variable Flow:</strong></para>
/// <code>
/// 1. BillingAgent tool sets userId (shared variable)
/// 2. MorganaContextProvider.SetVariable detects shared variable
/// 3. Invokes OnSharedContextUpdate callback
/// 4. BillingAgent.OnSharedContextUpdate broadcasts to RouterActor
/// 5. RouterActor sends ReceiveContextUpdate to all other agents
/// 6. ContractAgent receives and calls MergeSharedContext
/// 7. ContractAgent now has userId without asking user
/// </code>
/// </remarks>
public class MorganaAIContextProvider : AIContextProvider
{
    private readonly ILogger logger;

    /// <summary>
    /// Set of "shared" variable names, which are subject to the automatic broadcasting
    /// algorythm occurring between all the Morgana agents during tool's writing
    /// </summary>
    private ImmutableHashSet<string> SharedVariableNames;

    /// <summary>
    /// Source of truth for agent context variables (persistent across turns).
    /// Stores all conversation variables accessible via GetContextVariable/SetContextVariable tools.
    /// </summary>
    private ConcurrentDictionary<string, object> AgentContext = [];

    /// <summary>
    /// Callback invoked when a shared variable is set.
    /// Typically wired to MorganaAgent.OnSharedContextUpdate for broadcasting to RouterActor.
    /// </summary>
    public Action<string, object>? OnSharedContextUpdate { get; set; }

    /// <summary>
    /// Initializes a new instance of MorganaContextProvider.
    /// </summary>
    /// <param name="logger">Logger instance for context operation diagnostics</param>
    /// <param name="sharedVariableNames">
    /// Names of variables that should be broadcast to other agents when set.
    /// Extracted from tool definitions where Scope="context" and Shared=true.
    /// </param>
    public MorganaAIContextProvider(
        ILogger logger,
        IEnumerable<string>? sharedVariableNames = null)
    {
        this.logger = logger;
        this.SharedVariableNames = [.. sharedVariableNames ?? []];
    }

    /// <summary>
    /// Initializes a new instance of MorganaContextProvider from serialized state (deserialization).
    /// Used by AgentThread.DeserializeThread to restore provider state from persistence.
    /// </summary>
    /// <param name="logger">Logger instance for context operation diagnostics</param>
    /// <param name="serializedState">Serialized provider state from Serialize() method</param>
    /// <param name="jsonSerializerOptions">JSON serialization options (defaults to AgentAbstractionsJsonUtilities.DefaultOptions)</param>
    public MorganaAIContextProvider(
        ILogger logger,
        JsonElement serializedState,
        JsonSerializerOptions? jsonSerializerOptions = null)
    {
        this.logger = logger;

        jsonSerializerOptions ??= AgentAbstractionsJsonUtilities.DefaultOptions;

        // Deserialize AgentContext
        if (serializedState.TryGetProperty(nameof(AgentContext), out JsonElement contextElement))
        {
            AgentContext = contextElement.Deserialize<ConcurrentDictionary<string, object>>(jsonSerializerOptions) ?? [];
        }
        else
        {
            AgentContext = [];
        }

        // Deserialize SharedVariableNames
        if (serializedState.TryGetProperty(nameof(SharedVariableNames), out JsonElement sharedElement))
        {
            SharedVariableNames = sharedElement.Deserialize<ImmutableHashSet<string>>(jsonSerializerOptions) ?? [];
        }
        else
        {
            SharedVariableNames = [];
        }

        logger.LogInformation(
            $"{nameof(MorganaAIContextProvider)} DESERIALIZED {AgentContext.Count} variables and {SharedVariableNames.Count} shared names");
    }

    // =========================================================================
    // Agent Context
    // =========================================================================

    /// <summary>
    /// Retrieves a variable from the agent's persistent context.
    /// Used by GetContextVariable tool to check if information is already available.
    /// </summary>
    public object? GetVariable(string variableName)
    {
        if (AgentContext.TryGetValue(variableName, out object? value))
        {
            logger.LogInformation($"{nameof(MorganaAIContextProvider)} GET '{variableName}' = '{value}'");
            return value;
        }

        logger.LogInformation($"{nameof(MorganaAIContextProvider)} MISS '{variableName}'");
        return null;
    }

    /// <summary>
    /// Sets a variable in the agent's persistent context.
    /// If the variable is marked as shared, invokes the callback to notify RouterActor for broadcasting.
    /// </summary>
    public void SetVariable(string variableName, object variableValue)
    {
        AgentContext.AddOrUpdate(variableName, variableValue, (_, _) => variableValue);

        bool isShared = SharedVariableNames.Contains(variableName);

        logger.LogInformation(
            $"{nameof(MorganaAIContextProvider)} SET {(isShared ? "SHARED" : "PRIVATE")} '{variableName}' = '{variableValue}'");

        if (isShared)
            OnSharedContextUpdate?.Invoke(variableName, variableValue);
    }

    /// <summary>
    /// Removes a temporary variable from the agent's persistent context, freeing memory immediately.
    /// </summary>
    public void DropVariable(string variableName)
    {
        if (AgentContext.Remove(variableName, out _))
            logger.LogInformation($"{nameof(MorganaAIContextProvider)} DROPPED '{variableName}'");
    }

    /// <summary>
    /// Merges shared context variables received from other agents (peer-to-peer context synchronization).
    /// Uses first-write-wins strategy: accepts only variables not already present in local context.
    /// </summary>
    public void MergeSharedContext(Dictionary<string, object> sharedContext)
    {
        foreach (KeyValuePair<string, object> kvp in sharedContext)
        {
            if (!AgentContext.TryGetValue(kvp.Key, out object? value))
            {
                AgentContext[kvp.Key] = kvp.Value;

                logger.LogInformation(
                    $"{nameof(MorganaAIContextProvider)} MERGED shared context '{kvp.Key}' = '{kvp.Value}'");
            }
            else
            {
                logger.LogInformation(
                    $"{nameof(MorganaAIContextProvider)} IGNORED shared context '{kvp.Key}' (already set to '{value}')");
            }
        }
    }

    /// <summary>
    /// Propagates all shared context variables to other agents via OnSharedContextUpdate callback.
    /// Called after deserialization once the callback has been reconnected.
    /// </summary>
    /// <remarks>
    /// <para><strong>Purpose:</strong></para>
    /// <para>When an agent is reloaded from persistence, its shared variables need to be broadcast
    /// to other agents so they can receive the context. This method is called explicitly after
    /// the OnSharedContextUpdate callback has been reconnected in MorganaAgent.DeserializeThread.</para>
    /// <para><strong>Timing:</strong></para>
    /// <list type="number">
    /// <item>MorganaContextProvider constructor deserializes state (callback still NULL)</item>
    /// <item>MorganaAgent.DeserializeThread connects callback</item>
    /// <item>MorganaAgent.DeserializeThread calls PropagateSharedVariables()</item>
    /// <item>Shared variables broadcast to RouterActor</item>
    /// <item>RouterActor distributes to all other agents</item>
    /// </list>
    /// </remarks>
    public void PropagateSharedVariables()
    {
        int propagatedCount = 0;

        foreach (string sharedVariableName in SharedVariableNames)
        {
            if (AgentContext.TryGetValue(sharedVariableName, out object? sharedVariableValue))
            {
                OnSharedContextUpdate?.Invoke(sharedVariableName, sharedVariableValue);
                propagatedCount++;

                logger.LogInformation(
                    $"{nameof(MorganaAIContextProvider)} PROPAGATED shared variable '{sharedVariableName}' = '{sharedVariableValue}'");
            }
        }

        if (propagatedCount > 0)
        {
            logger.LogInformation(
                $"{nameof(MorganaAIContextProvider)} PROPAGATED {propagatedCount} shared variables to other agents");
        }
    }

    /// <summary>
    /// Restores the internal state of this provider from serialized data.
    /// Used during deserialization to update the existing provider instance instead of creating a new one.
    /// This preserves tool closures that captured the provider instance.
    /// </summary>
    /// <param name="serializedState">Serialized provider state from persistence</param>
    /// <param name="jsonSerializerOptions">JSON serialization options</param>
    /// <remarks>
    /// <para><strong>Why This Exists:</strong></para>
    /// <para>Tools are created with closures that capture the contextProvider field:
    /// <code>
    /// () => contextProvider
    /// </code></para>
    /// <para>If we create a new provider instance during deserialization, the tool closures
    /// still point to the old instance, causing a mismatch where tools write to one instance
    /// but the agent reads from another.</para>
    /// <para>By restoring state into the existing instance, we preserve the tool closures.</para>
    /// </remarks>
    public void RestoreState(JsonElement serializedState, JsonSerializerOptions? jsonSerializerOptions = null)
    {
        jsonSerializerOptions ??= AgentAbstractionsJsonUtilities.DefaultOptions;

        // Restore AgentContext
        if (serializedState.TryGetProperty(nameof(AgentContext), out JsonElement agentContextJsonElement))
        {
            ConcurrentDictionary<string, object>? agentContext = agentContextJsonElement.Deserialize<ConcurrentDictionary<string, object>>(jsonSerializerOptions);
            if (agentContext != null)
                AgentContext = agentContext;
        }

        // Restore SharedVariableNames
        if (serializedState.TryGetProperty(nameof(SharedVariableNames), out JsonElement sharedVariableNamesJsonElement))
        {
            ImmutableHashSet<string>? sharedVariableNames = sharedVariableNamesJsonElement.Deserialize<ImmutableHashSet<string>>(jsonSerializerOptions);
            if (sharedVariableNames != null)
                SharedVariableNames = sharedVariableNames;
        }

        logger.LogInformation(
            $"{nameof(MorganaAIContextProvider)} CONTEXT RESTORED: {AgentContext.Count} variables and {SharedVariableNames.Count} shared names");
    }

    // =========================================================================
    // AIContextProvider (Microsoft.Agents.AI)
    // =========================================================================

    /// <summary>
    /// Serializes the provider's internal state for persistence with AgentThread.
    /// </summary>
    public override JsonElement Serialize(JsonSerializerOptions? jsonSerializerOptions = null)
    {
        logger.LogInformation(
            $"{nameof(MorganaAIContextProvider)} SERIALIZING {AgentContext.Count} variables and {SharedVariableNames.Count} shared names");

        jsonSerializerOptions ??= AgentAbstractionsJsonUtilities.DefaultOptions;

        return JsonSerializer.SerializeToElement(
            new Dictionary<string, JsonElement>
            {
                { nameof(AgentContext), JsonSerializer.SerializeToElement(AgentContext, jsonSerializerOptions) },
                { nameof(SharedVariableNames), JsonSerializer.SerializeToElement(SharedVariableNames, jsonSerializerOptions) }
            }, jsonSerializerOptions);
    }

    /// <summary>
    /// Hook called BEFORE agent invocation (USER -> AGENT)
    /// </summary>
    public override ValueTask<AIContext> InvokingAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        // For future usage: this AIContext can be given ephemeral instructions
        // which will be visible to the agent only during this single roundtrip.
        // It is useful for injecting transient LLM conditioning strategies...

        return ValueTask.FromResult(new AIContext());
    }

    /// <summary>
    /// Hook called AFTER agent invocation (AGENT -> USER)
    /// </summary>
    public override ValueTask InvokedAsync(
        InvokedContext context,
        CancellationToken cancellationToken = default)
    {
        // For future usage: the context we are receiving is returned by the agent
        // at the end of this single roundtrip. It is useful for inspecting response
        // messages and executing targeted context updates based on it...

        return base.InvokedAsync(context, cancellationToken);
    }
}