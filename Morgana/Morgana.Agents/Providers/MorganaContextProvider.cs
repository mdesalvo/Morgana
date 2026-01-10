using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;

namespace Morgana.Agents.Providers;

/// <summary>
/// Custom AIContextProvider that maintains agent conversation context variables.
/// Supports automatic serialization/deserialization for persistence with AgentThread and
/// cross-agent context sharing via RouterActor broadcast mechanism.
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
public class MorganaContextProvider : AIContextProvider
{
    private readonly ILogger logger;
    private readonly HashSet<string> sharedVariableNames;

    /// <summary>
    /// Source of truth for agent context variables.
    /// Stores all conversation variables accessible via GetContextVariable/SetContextVariable tools.
    /// </summary>
    public Dictionary<string, object> AgentContext { get; private set; } = [];

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
    public MorganaContextProvider(
        ILogger logger,
        IEnumerable<string>? sharedVariableNames = null)
    {
        this.logger = logger;
        this.sharedVariableNames = new HashSet<string>(sharedVariableNames ?? []);
    }

    /// <summary>
    /// Retrieves a variable from the agent's context.
    /// Used by GetContextVariable tool to check if information is already available.
    /// </summary>
    /// <param name="variableName">Name of the variable to retrieve (e.g., "userId", "invoiceId")</param>
    /// <returns>Variable value if found, null if not present in context</returns>
    /// <remarks>
    /// <para><strong>Logging Behavior:</strong></para>
    /// <list type="bullet">
    /// <item><term>HIT</term><description>Variable found, logs name and value</description></item>
    /// <item><term>MISS</term><description>Variable not found, logs name only</description></item>
    /// </list>
    /// <para><strong>Usage by Tools:</strong></para>
    /// <code>
    /// // MorganaTool.GetContextVariable
    /// public Task&lt;object&gt; GetContextVariable(string variableName)
    /// {
    ///     MorganaContextProvider provider = getContextProvider();
    ///     object? value = provider.GetVariable(variableName);
    ///
    ///     if (value != null)
    ///     {
    ///         // HIT: Return value to LLM
    ///         return Task.FromResult(value);
    ///     }
    ///
    ///     // MISS: Return instruction to LLM
    ///     return Task.FromResult&lt;object&gt;(
    ///         $"Information {variableName} not available in context: you need to engage SetContextVariable to set it."
    ///     );
    /// }
    /// </code>
    /// </remarks>
    public object? GetVariable(string variableName)
    {
        if (AgentContext.TryGetValue(variableName, out object? value))
        {
            logger.LogInformation($"MorganaContextProvider GET '{variableName}' = '{value}'");
            return value;
        }

        logger.LogInformation($"MorganaContextProvider MISS '{variableName}'");
        return null;
    }

    /// <summary>
    /// Sets a variable in the agent's context.
    /// If the variable is marked as shared, invokes the callback to notify RouterActor for broadcasting.
    /// </summary>
    /// <param name="variableName">Name of the variable to set (e.g., "userId")</param>
    /// <param name="variableValue">Value to store</param>
    /// <remarks>
    /// <para><strong>Variable Classification:</strong></para>
    /// <list type="bullet">
    /// <item><term>PRIVATE</term><description>Variable only used by this agent (no broadcast)</description></item>
    /// <item><term>SHARED</term><description>Variable broadcast to all other agents via RouterActor</description></item>
    /// </list>
    /// <para><strong>Shared Variable Determination:</strong></para>
    /// <para>A variable is shared if its name appears in the sharedVariableNames HashSet,
    /// which is populated from tool definitions where Shared=true in agents.json.</para>
    /// <para><strong>Broadcast Flow (Shared Variables):</strong></para>
    /// <code>
    /// 1. SetVariable("userId", "P994E") called
    /// 2. Check if "userId" in sharedVariableNames → YES
    /// 3. Store in AgentContext: {"userId": "P994E"}
    /// 4. Invoke OnSharedContextUpdate("userId", "P994E")
    /// 5. MorganaAgent.OnSharedContextUpdate receives callback
    /// 6. Agent tells RouterActor: BroadcastContextUpdate
    /// 7. RouterActor sends to all agents: ReceiveContextUpdate
    /// 8. Other agents call MergeSharedContext
    /// </code>
    /// <para><strong>Usage by Tools:</strong></para>
    /// <code>
    /// // MorganaTool.SetContextVariable
    /// public Task&lt;object&gt; SetContextVariable(string variableName, string variableValue)
    /// {
    ///     MorganaContextProvider provider = getContextProvider();
    ///     provider.SetVariable(variableName, variableValue);
    ///     // Automatic broadcast if shared variable
    ///
    ///     return Task.FromResult&lt;object&gt;(
    ///         $"Information {variableName} inserted in context with value: {variableValue}"
    ///     );
    /// }
    /// </code>
    /// </remarks>
    public void SetVariable(string variableName, object variableValue)
    {
        AgentContext[variableName] = variableValue;

        bool isShared = sharedVariableNames.Contains(variableName);

        logger.LogInformation($"MorganaContextProvider SET {(isShared ? "SHARED" : "PRIVATE")} '{variableName}' = '{variableValue}'");

        if (isShared)
        {
            OnSharedContextUpdate?.Invoke(variableName, variableValue);
        }
    }

    /// <summary>
    /// Removes a temporary variable from the agent's context, freeing memory immediately.
    /// Use this for ephemeral variables that are no longer needed (e.g., "__pending_quick_replies").
    /// </summary>
    /// <param name="variableName">Name of the variable to remove from context</param>
    /// <remarks>
    /// <para><strong>When to use DropVariable vs SetVariable(null):</strong></para>
    /// <list type="bullet">
    /// <item><term>DropVariable</term><description>Removes the key entirely - use for temporary/ephemeral data</description></item>
    /// <item><term>SetVariable(null)</term><description>Sets value to null but keeps the key - use for persistent variables</description></item>
    /// </list>
    /// <para><strong>Typical usage:</strong></para>
    /// <code>
    /// // Set temporary variable
    /// contextProvider.SetVariable("__pending_quick_replies", jsonData);
    ///
    /// // Process the data
    /// var data = contextProvider.GetVariable("__pending_quick_replies");
    ///
    /// // Drop immediately after use (temporary variable pattern)
    /// contextProvider.DropVariable("__pending_quick_replies");
    /// </code>
    /// <para><strong>Convention:</strong> Prefix temporary variables with "__" (double underscore) to signal ephemeral nature.</para>
    /// </remarks>
    public void DropVariable(string variableName)
    {
        AgentContext.Remove(variableName);

        logger.LogInformation($"MorganaContextProvider DROP '{variableName}'");
    }

    /// <summary>
    /// Merges shared context variables received from other agents (peer-to-peer context synchronization).
    /// Uses first-write-wins strategy: accepts only variables not already present in local context.
    /// </summary>
    /// <param name="sharedContext">Dictionary of shared variables from another agent</param>
    /// <remarks>
    /// <para><strong>First-Write-Wins Strategy:</strong></para>
    /// <para>If a variable already exists in the local context, the incoming value is ignored.
    /// This prevents conflicts when multiple agents independently discover the same information.</para>
    /// <para><strong>Merge Scenarios:</strong></para>
    /// <code>
    /// // Scenario 1: Variable not present (MERGED)
    /// Local context: {}
    /// Incoming: {"userId": "P994E"}
    /// Result: {"userId": "P994E"}
    ///
    /// // Scenario 2: Variable already present (IGNORED)
    /// Local context: {"userId": "P994E"}
    /// Incoming: {"userId": "Q123Z"}  // Different value!
    /// Result: {"userId": "P994E"}    // Keeps original value
    ///
    /// // Scenario 3: Multiple variables, partial merge
    /// Local context: {"userId": "P994E"}
    /// Incoming: {"userId": "Q123Z", "invoiceId": "INV-001"}
    /// Result: {"userId": "P994E", "invoiceId": "INV-001"}
    ///         // Kept userId, merged invoiceId
    /// </code>
    /// <para><strong>Usage in MorganaAgent:</strong></para>
    /// <code>
    /// private void HandleContextUpdate(Records.ReceiveContextUpdate msg)
    /// {
    ///     string myIntent = GetType().GetCustomAttribute&lt;HandlesIntentAttribute&gt;()?.Intent ?? "unknown";
    ///
    ///     agentLogger.LogInformation(
    ///         $"Agent '{myIntent}' received shared context from '{msg.SourceAgentIntent}': " +
    ///         $"{string.Join(", ", msg.UpdatedValues.Keys)}");
    ///
    ///     contextProvider.MergeSharedContext(msg.UpdatedValues);
    /// }
    /// </code>
    /// <para><strong>Conflict Resolution Rationale:</strong></para>
    /// <para>First-write-wins prevents race conditions where multiple agents ask the user for the same
    /// information simultaneously. The first agent to receive the answer "wins" and shares with others.</para>
    /// </remarks>
    public void MergeSharedContext(Dictionary<string, object> sharedContext)
    {
        foreach (KeyValuePair<string, object> kvp in sharedContext)
        {
            if (!AgentContext.TryGetValue(kvp.Key, out object? value))
            {
                AgentContext[kvp.Key] = kvp.Value;

                logger.LogInformation($"MorganaContextProvider MERGED shared context '{kvp.Key}' = '{kvp.Value}'");
            }
            else
            {
                logger.LogInformation($"MorganaContextProvider IGNORED shared context '{kvp.Key}' (already set to '{value}')");
            }
        }
    }

    /// <summary>
    /// Serializes the provider's state for persistence with AgentThread.
    /// Enables conversation context to survive across thread restarts or serialization cycles.
    /// </summary>
    /// <returns>JSON string containing AgentContext and SharedVariableNames</returns>
    /// <remarks>
    /// <para><strong>Serialization Format:</strong></para>
    /// <code>
    /// {
    ///   "AgentContext": {
    ///     "userId": "P994E",
    ///     "invoiceId": "INV-2024-001"
    ///   },
    ///   "SharedVariableNames": ["userId"]
    /// }
    /// </code>
    /// <para><strong>Usage with AgentThread:</strong></para>
    /// <para>This method supports AgentThread persistence, allowing conversation state to be saved
    /// and restored across sessions. Currently not actively used but available for future enhancements.</para>
    /// </remarks>
    public string Serialize()
    {
        return JsonSerializer.Serialize(new
        {
            AgentContext,
            SharedVariableNames = sharedVariableNames
        });
    }

    /// <summary>
    /// Deserializes provider state from AgentThread persistence.
    /// Reconstructs a MorganaContextProvider with saved context variables.
    /// </summary>
    /// <param name="json">JSON string from Serialize method</param>
    /// <param name="logger">Logger instance for the restored provider</param>
    /// <returns>Restored MorganaContextProvider instance with context state</returns>
    /// <remarks>
    /// <para><strong>Deserialization Flow:</strong></para>
    /// <list type="number">
    /// <item>Parse JSON into dictionary structure</item>
    /// <item>Extract AgentContext variables</item>
    /// <item>Extract SharedVariableNames configuration</item>
    /// <item>Create new MorganaContextProvider with restored state</item>
    /// <item>Log number of variables restored</item>
    /// </list>
    /// <para><strong>Usage Pattern:</strong></para>
    /// <code>
    /// // Serialize on thread save
    /// string json = contextProvider.Serialize();
    /// // Store json with AgentThread
    ///
    /// // Deserialize on thread restore
    /// MorganaContextProvider restored = MorganaContextProvider.Deserialize(json, logger);
    /// // Continue conversation with restored context
    /// </code>
    /// <para><strong>Note:</strong></para>
    /// <para>Currently not actively used by the framework but available for future persistence features
    /// (e.g., saving conversation state to database, resuming conversations across server restarts).</para>
    /// </remarks>
    public static MorganaContextProvider Deserialize(string json, ILogger logger)
    {
        Dictionary<string, JsonElement>? data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);

        Dictionary<string, object> agentContext = data?["AgentContext"].Deserialize<Dictionary<string, object>>() ?? [];

        HashSet<string> sharedVars = data?["SharedVariableNames"].Deserialize<HashSet<string>>() ?? [];

        MorganaContextProvider provider = new MorganaContextProvider(logger, sharedVars)
        {
            AgentContext = agentContext
        };

        logger.LogInformation(
            $"MorganaContextProvider DESERIALIZED with {agentContext.Count} variables");

        return provider;
    }

    // =========================================================================
    // AIContextProvider Implementation (Microsoft.Agents.AI)
    // =========================================================================

    /// <summary>
    /// AIContextProvider hook: called before agent invocation.
    /// Currently returns empty context. Reserved for future enhancements like
    /// injecting context variables as system messages.
    /// </summary>
    /// <param name="context">Invoking context from Microsoft.Agents.AI</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>AIContext for agent invocation (currently empty)</returns>
    /// <remarks>
    /// <para><strong>Future Enhancement Opportunity:</strong></para>
    /// <para>This hook could inject important context variables as system messages before each LLM call:</para>
    /// <code>
    /// // Potential future implementation
    /// public override ValueTask&lt;AIContext&gt; InvokingAsync(InvokingContext context, CancellationToken ct)
    /// {
    ///     AIContext aiContext = new AIContext();
    ///
    ///     if (AgentContext.ContainsKey("userId"))
    ///     {
    ///         aiContext.AddSystemMessage($"User ID: {AgentContext["userId"]}");
    ///     }
    ///
    ///     return ValueTask.FromResult(aiContext);
    /// }
    /// </code>
    /// </remarks>
    public override ValueTask<AIContext> InvokingAsync(InvokingContext context, CancellationToken cancellationToken = default)
    {
        logger.LogInformation($"{nameof(MorganaContextProvider)} is invoking LLM. ({context.RequestMessages?.Count() ?? 0} request messages)");

        // Could inject context variables as system messages if needed in the future
        return ValueTask.FromResult(new AIContext());
    }

    /// <summary>
    /// AIContextProvider hook: called after agent invocation.
    /// Currently performs no action. Reserved for future enhancements like
    /// inspecting or logging agent responses.
    /// </summary>
    /// <param name="context">Invoked context from Microsoft.Agents.AI</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Completed task</returns>
    /// <remarks>
    /// <para><strong>Future Enhancement Opportunity:</strong></para>
    /// <para>This hook could inspect agent responses for analytics or debugging:</para>
    /// <code>
    /// // Potential future implementation
    /// public override ValueTask InvokedAsync(InvokedContext context, CancellationToken ct)
    /// {
    ///     logger.LogInformation($"Agent response length: {context.Response.Text.Length} chars");
    ///     logger.LogInformation($"Tool calls made: {context.ToolCalls.Count}");
    ///     return base.InvokedAsync(context, ct);
    /// }
    /// </code>
    /// </remarks>
    public override ValueTask InvokedAsync(InvokedContext context, CancellationToken cancellationToken = default)
    {
        logger.LogInformation($"{nameof(MorganaContextProvider)} has invoked LLM. ({context.ResponseMessages?.Count() ?? 0} response messages)");

        // Could inspect/log agent responses if needed in the future
        return base.InvokedAsync(context, cancellationToken);
    }
}