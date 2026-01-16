using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;

namespace Morgana.Framework.Providers;

/// <summary>
/// Custom AIContextProvider that maintains agent conversation context variables.
/// Supports automatic serialization/deserialization for persistence with AgentThread,
/// cross-agent context sharing via RouterActor broadcast mechanism and ephemeral
/// instructions for single-turn agent conditioning.
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
/// <remarks>
/// <para>Tools can inject single-use instructions that condition the agent for the
/// next turn only. These instructions are automatically injected before agent invocation
/// and cleared immediately after, preventing context pollution.</para>
/// <para><strong>Use Cases:</strong></para>
/// <list type="bullet">
/// <item><term>Dynamic Data</term><description>Tool discovers invoices and adds summary for LLM awareness</description></item>
/// <item><term>Conditional Guardrails</term><description>Tool detects overdue invoices and adds warning instruction</description></item>
/// <item><term>Behavioral Hints</term><description>Agent detects long conversation and suggests summarization</description></item>
/// </list>
/// </remarks>
public class MorganaContextProvider : AIContextProvider
{
    private readonly ILogger logger;

    /// <summary>
    /// Set of "shared" variable names, which are subject to the automatic broadcasting
    /// algorythm occurring between all the Morgana agents during tool's writing
    /// </summary>
    private HashSet<string> SharedVariableNames;

    /// <summary>
    /// Source of truth for agent context variables (persistent across turns).
    /// Stores all conversation variables accessible via GetContextVariable/SetContextVariable tools.
    /// </summary>
    private Dictionary<string, object> AgentContext = [];

    /// <summary>
    /// Ephemeral instructions that will be injected ONLY in the next agent invocation.
    /// Automatically cleared after injection to prevent persistent context pollution.
    /// Key = instruction identifier (e.g., "TOOL_RESULT", "GUARDRAIL")
    /// Value = instruction text to inject
    /// </summary>
    private Dictionary<string, string> EphemeralContext = [];

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
        this.SharedVariableNames = [.. sharedVariableNames ?? []];
    }

    // =========================================================================
    // Ephemeral Context (single-turn agent conditioning)
    // =========================================================================

    /// <summary>
    /// Adds an ephemeral instruction that will be injected ONLY in the next agent invocation.
    /// Instructions are automatically cleared after injection to prevent agent context pollution.
    /// </summary>
    /// <param name="key">
    /// Instruction identifier (e.g., "TOOL_RESULT", "OVERDUE_ALERT", "USER_TIER", ...).
    /// Used as category prefix in the injected instruction.
    /// </param>
    /// <param name="instruction">
    /// Instruction text to inject. Should be concise and actionable.
    /// Examples:
    /// - "User has 3 recent invoices. Latest invoice: INV-2024-001 for €1,250.00"
    /// - "WARNING: User has overdue invoices. Offer payment assistance."
    /// - "User tier: PREMIUM. Adjust tone and offer premium features."
    /// </param>
    /// <remarks>
    /// <para><strong>Design Philosophy:</strong></para>
    /// <para>Ephemeral instructions are "single-use hints" that tools inject to condition
    /// the agent for the next turn only. They don't persist in AgentContext and don't
    /// affect serialization or cross-agent sharing.</para>
    /// <para><strong>When to Use:</strong></para>
    /// <list type="bullet">
    /// <item>Tool discovers agentThread that LLM should be aware of (invoice counts, user status, ...)</item>
    /// <item>Tool detects condition requiring special handling (overdue payments, tier changes, ...)</item>
    /// <item>Agent wants to guide LLM behavior for current turn (long conversation hint)</item>
    /// </list>
    /// <para><strong>Key Naming Conventions:</strong></para>
    /// <list type="bullet">
    /// <item><term>TOOL_RESULT</term><description>Data discovered by tool execution</description></item>
    /// <item><term>GUARDRAIL</term><description>Policy or safety constraint</description></item>
    /// <item><term>HINT</term><description>Behavioral suggestion</description></item>
    /// <item><term>ALERT</term><description>Urgent condition requiring attention</description></item>
    /// <item><term>CONTEXT_ENRICHMENT</term><description>Additional context from external systems</description></item>
    /// </list>
    /// <para><strong>Overwrite Behavior:</strong></para>
    /// <para>If the same key is set multiple times before agent invocation (e.g., multiple tool
    /// calls in same turn), the last value wins. This is intentional - later tools have more
    /// complete information.</para>
    /// </remarks>
    public void AddEphemeralInstruction(string key, string instruction)
    {
        EphemeralContext[key] = instruction;

        // Log with truncation for readability (full instruction logged during injection)
        string preview = instruction.Length > 80
            ? instruction[..77] + "..."
            : instruction;

        logger.LogInformation(
            $"{nameof(MorganaContextProvider)} ADDED ephemeral instruction '{key}': {preview}");
    }

    /// <summary>
    /// Removes an ephemeral instruction before the next agent invocation.
    /// Typically not needed, since instructions are auto-cleared after injection.
    /// Use this if a tool needs to explicitly cancel an instruction set by a previous tool in the same turn.
    /// </summary>
    /// <param name="key">Instruction identifier to remove</param>
    /// <remarks>
    /// <para><strong>Rare Usage Scenario:</strong></para>
    /// <code>
    /// // Tool A sets an alert
    /// provider.AddEphemeralInstruction("ALERT", "User needs assistance");
    ///
    /// // Tool B discovers issue resolved, cancels alert
    /// provider.RemoveEphemeralInstruction("ALERT");
    /// </code>
    /// </remarks>
    public void RemoveEphemeralInstruction(string key)
    {
        if (EphemeralContext.Remove(key))
        {
            logger.LogInformation($"{nameof(MorganaContextProvider)} REMOVED ephemeral instruction '{key}'");
        }
    }

    /// <summary>
    /// Clears all ephemeral instructions to reset agent conditioning.
    /// Called automatically by InvokingAsync after injection.
    /// Can be called manually if needed (e.g., conversation reset).
    /// </summary>
    public void ClearEphemeralInstructions()
    {
        int count = EphemeralContext.Count;
        EphemeralContext.Clear();

        if (count > 0)
        {
            logger.LogInformation(
                $"{nameof(MorganaContextProvider)} CLEARED {count} ephemeral instructions");
        }
    }

    // =========================================================================
    // Agent Context (multi-turn agent conditioning)
    // =========================================================================

    /// <summary>
    /// Retrieves a variable from the agent's persistent context.
    /// Used by GetContextVariable tool to check if information is already available.
    /// </summary>
    public object? GetVariable(string variableName)
    {
        if (AgentContext.TryGetValue(variableName, out object? value))
        {
            logger.LogInformation($"{nameof(MorganaContextProvider)} GET '{variableName}' = '{value}'");
            return value;
        }

        logger.LogInformation($"{nameof(MorganaContextProvider)} MISS '{variableName}'");
        return null;
    }

    /// <summary>
    /// Sets a variable in the agent's persistent context.
    /// If the variable is marked as shared, invokes the callback to notify RouterActor for broadcasting.
    /// </summary>
    public void SetVariable(string variableName, object variableValue)
    {
        AgentContext[variableName] = variableValue;

        bool isShared = SharedVariableNames.Contains(variableName);

        logger.LogInformation(
            $"{nameof(MorganaContextProvider)} SET {(isShared ? "SHARED" : "PRIVATE")} '{variableName}' = '{variableValue}'");

        if (isShared)
            OnSharedContextUpdate?.Invoke(variableName, variableValue);
    }

    /// <summary>
    /// Removes a temporary variable from the agent's persistent context, freeing memory immediately.
    /// </summary>
    public void DropVariable(string variableName)
    {
        AgentContext.Remove(variableName);

        logger.LogInformation($"{nameof(MorganaContextProvider)} DROPPED '{variableName}'");
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
                    $"{nameof(MorganaContextProvider)} MERGED shared context '{kvp.Key}' = '{kvp.Value}'");
            }
            else
            {
                logger.LogInformation(
                    $"{nameof(MorganaContextProvider)} IGNORED shared context '{kvp.Key}' (already set to '{value}')");
            }
        }
    }

    // =========================================================================
    // Serialization
    // =========================================================================

    /// <summary>
    /// Serializes the provider's internal state for persistence with AgentThread.
    /// NOTE: ephemeral instructions are NOT serialized (by design - they're ephemeral).
    /// </summary>
    public override JsonElement Serialize(JsonSerializerOptions? jsonSerializerOptions = null)
    {
        logger.LogInformation(
            $"{nameof(MorganaContextProvider)} SERIALIZING with {AgentContext.Count} variables and {SharedVariableNames.Count} shared names");

        jsonSerializerOptions ??= AgentAbstractionsJsonUtilities.DefaultOptions;

        return JsonSerializer.SerializeToElement(
            new Dictionary<string, JsonElement>
            {
                { nameof(AgentContext), JsonSerializer.SerializeToElement(AgentContext, jsonSerializerOptions) },
                { nameof(SharedVariableNames), JsonSerializer.SerializeToElement(SharedVariableNames, jsonSerializerOptions) }
            }, jsonSerializerOptions);
    }

    /// <summary>
    /// Deserializes provider state from AgentThread persistence.
    /// </summary>
    public static MorganaContextProvider Deserialize(string json, ILogger logger)
    {
        #region JsonElement Utilities
        /* Converts a JsonElement to its appropriate .NET type.
         * Handles: string, number, boolean, null, object, array. */
        object ConvertJsonElementToObject(JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.String => element.GetString()!,
                JsonValueKind.Number => element.TryGetInt32(out int intValue)
                    ? intValue
                    : element.TryGetInt64(out long longValue)
                        ? longValue
                        : element.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null!,
                JsonValueKind.Object => DeserializeJsonObject(element),
                JsonValueKind.Array => DeserializeJsonArray(element),
                _ => element.GetRawText() // Fallback to raw JSON string
            };
        }

        /* Deserializes a JsonElement representing an object to Dictionary<string, object> */
        Dictionary<string, object> DeserializeJsonObject(JsonElement element)
        {
            Dictionary<string, object> result = [];

            foreach (JsonProperty property in element.EnumerateObject())
                result[property.Name] = ConvertJsonElementToObject(property.Value);

            return result;
        }

        /* Deserializes a JsonElement representing an array to List<object> */
        List<object> DeserializeJsonArray(JsonElement element)
        {
            List<object> result = [];

            foreach (JsonElement item in element.EnumerateArray())
                result.Add(ConvertJsonElementToObject(item));

            return result;
        }
        #endregion

        JsonSerializerOptions jsonSerializerOptions = AgentAbstractionsJsonUtilities.DefaultOptions;

        // Parse the JSON to get the root dictionary
        Dictionary<string, JsonElement>? agentThread = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            json, jsonSerializerOptions);

        if (agentThread == null)
        {
            logger.LogWarning($"{nameof(MorganaContextProvider)} DESERIALIZED with NULL agentThread - creating empty provider");
            return new MorganaContextProvider(logger);
        }

        // Deserialize AgentContext
        Dictionary<string, object> agentContext = new Dictionary<string, object>();
        if (agentThread.TryGetValue(nameof(AgentContext), out JsonElement agentContextElement)
             && agentContextElement.ValueKind == JsonValueKind.Object)
        {
            // JsonElement must be explicitly converted to Dictionary<string, object>
            foreach (JsonProperty property in agentContextElement.EnumerateObject())
                agentContext[property.Name] = ConvertJsonElementToObject(property.Value);
        }

        // Deserialize SharedVariableNames
        HashSet<string> sharedVariableNames = [];
        if (agentThread.TryGetValue(nameof(SharedVariableNames), out JsonElement sharedNamesElement))
        {
            // JsonElement can be directly deserialized to HashSet<string>
            sharedVariableNames = sharedNamesElement.Deserialize<HashSet<string>>(jsonSerializerOptions) ?? [];
        }

        logger.LogInformation(
            $"{nameof(MorganaContextProvider)} DESERIALIZED with {agentContext.Count} variables and {sharedVariableNames.Count} shared names");

        return new MorganaContextProvider(logger)
        {
            AgentContext = agentContext,
            SharedVariableNames = sharedVariableNames
        };
    }

    // =========================================================================
    // AIContextProvider (Microsoft.Agents.AI)
    // =========================================================================

    /// <summary>
    /// AIContextProvider hook: called BEFORE agent invocation.
    /// Injects ephemeral instructions and clears them immediately after.
    /// </summary>
    /// <remarks>
    /// <para><strong>Injection Format (Markdown-based):</strong></para>
    /// <code>
    /// ---BEGIN EPHEMERAL CONTEXT---
    /// ---Consider the following instructions as 'ephemeral hints' giving you insights and agentThread relevant ONLY for this turn---
    /// [AVAILABLE_INVOICES] User has 3 recent invoices. Latest invoice: INV-2024-001 for €1,250.00
    ///
    /// [OVERDUE_ALERT] WARNING: User has 2 overdue invoices totaling €3,400. Offer payment assistance.
    /// ---END EPHEMERAL CONTEXT---
    /// </code>
    /// <para><strong>Automatic Cleanup:</strong></para>
    /// <para>Instructions are cleared immediately after injection to prevent:
    /// - Context pollution across turns
    /// - Stale instructions affecting future invocations
    /// - Accidental serialization with AgentThread
    /// </para>
    /// </remarks>
    public override ValueTask<AIContext> InvokingAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            $"{nameof(MorganaContextProvider)} is invoking LLM. " +
            $"({context.RequestMessages?.Count() ?? 0} request messages, " +
            $"{EphemeralContext.Count} ephemeral instruction(s))");

        AIContext aiContext = new AIContext();

        // Inject ephemeral instructions
        if (EphemeralContext.Count > 0)
        {
            StringBuilder sb = new StringBuilder();

            sb.AppendLine("---BEGIN EPHEMERAL CONTEXT---");
            sb.AppendLine("---Consider the following instructions as 'ephemeral hints' giving you insights and agentThread relevant ONLY for this turn.---");
            foreach (KeyValuePair<string, string> kvp in EphemeralContext)
            {
                sb.AppendLine($"[{kvp.Key}] {kvp.Value}");

                logger.LogInformation(
                    $"Injecting ephemeral instruction [{kvp.Key}]: {kvp.Value}");
            }
            sb.AppendLine("---END EPHEMERAL CONTEXT---");

            aiContext.Instructions = sb.ToString();

            logger.LogInformation(
                $"Injected {EphemeralContext.Count} ephemeral instruction(s) into LLM context");

            // Clear immediately after injection (single-use)
            ClearEphemeralInstructions();
        }

        return ValueTask.FromResult(aiContext);
    }

    /// <summary>
    /// AIContextProvider hook: called AFTER agent invocation.
    /// Currently performs no action. Reserved for future enhancements.
    /// </summary>
    public override ValueTask InvokedAsync(
        InvokedContext context,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            $"{nameof(MorganaContextProvider)} has invoked LLM. " +
            $"({context.ResponseMessages?.Count() ?? 0} response messages)");

        return base.InvokedAsync(context, cancellationToken);
    }
}