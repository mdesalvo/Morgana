using Microsoft.Extensions.Logging;
using Morgana.AI.Providers;

namespace Morgana.AI.Tools;

/// <summary>
/// Base class for agent tools that provides context variable access (Get/Set operations).
/// Tools can read from and write to the agent's conversation context via MorganaContextProvider.
/// </summary>
/// <remarks>
/// <para><strong>Purpose:</strong></para>
/// <para>MorganaTool provides a foundation for building tools that agents can call during LLM interactions.
/// The most critical feature is context variable management, which allows agents to remember information
/// across multiple turns and share information with other agents.</para>
/// <para><strong>Architecture:</strong></para>
/// <code>
/// MorganaTool (base class with context access)
///   ├── BillingTool (domain-specific tool for billing)
///   ├── ContractTool (domain-specific tool for contracts)
///   ├── TroubleshootingTool (domain-specific tool for troubleshooting)
///   └── ... other custom tools
/// </code>
/// <para><strong>Context Variable Types:</strong></para>
/// <list type="bullet">
/// <item><term>Local variables</term><description>Agent-specific, not shared with other agents</description></item>
/// <item><term>Shared variables</term><description>Broadcast to all agents via RouterActor for cross-agent coordination</description></item>
/// </list>
/// <para><strong>Usage Pattern:</strong></para>
/// <para>Tools inherit from MorganaTool and use GetContextVariable/SetContextVariable to interact with
/// conversation context. The LLM decides when to call these operations based on agent prompts.</para>
/// <para><strong>Example:</strong></para>
/// <code>
/// [ProvidesToolForIntent("billing")]
/// public class BillingTool : MorganaTool
/// {
///     private readonly string[] invoices =
///     [
///        "InvoiceID: A555 - Period: Oct 2025 / Nov 2025 - Amount: €130 - Status: Pending (due date 2025-12-15)",
///        "InvoiceID: B222 - Period: Sep 2025 / Oct 2025 - Amount: €150 - Status: Paid (on 2025-11-14)",
///        "InvoiceID: C333 - Period: Jun 2025 / Sep 2025 - Amount: €125 - Status: Paid (on 2025-10-13)",
///        "InvoiceID: Z999 - Period: May 2025 / Jun 2025 - Amount: €100 - Status: Paid (on 2025-09-13)"
///     ];
///
///     public async Task&lt;InvoiceList&gt; GetInvoices(string userId, int count)
///     {
///         await Task.Delay(50);
///
///         return string.Join("\n", invoices.Take(count));
///     }
/// }
/// </code>
/// </remarks>
public class MorganaTool
{
    /// <summary>
    /// Logger instance for tool-level diagnostics and context operation tracking.
    /// </summary>
    protected readonly ILogger toolLogger;

    /// <summary>
    /// Factory function to retrieve the current MorganaContextProvider instance.
    /// Uses a function to enable lazy evaluation and ensure correct scoping per request.
    /// </summary>
    protected readonly Func<MorganaContextProvider> getContextProvider;

    /// <summary>
    /// Initializes a new instance of MorganaTool with logging and context provider access.
    /// </summary>
    /// <param name="toolLogger">Logger instance for tool diagnostics</param>
    /// <param name="getContextProvider">Factory function to retrieve the context provider</param>
    /// <remarks>
    /// The context provider is passed as a Func to ensure each tool call gets the correct
    /// scoped provider instance for the current conversation and agent.
    /// </remarks>
    public MorganaTool(
        ILogger toolLogger,
        Func<MorganaContextProvider> getContextProvider)
    {
        this.toolLogger = toolLogger;
        this.getContextProvider = getContextProvider;
    }

    /// <summary>
    /// Retrieves a context variable from the agent's conversation context.
    /// Used by agents (via LLM tool calling) to check if information is already available before asking the user.
    /// </summary>
    /// <param name="variableName">Name of the context variable to retrieve (e.g., "userId", "invoiceId")</param>
    /// <returns>
    /// Task containing the variable value if found, or an instructional message if missing.
    /// The LLM interprets the returned message and decides whether to call SetContextVariable or ask the user.
    /// </returns>
    /// <remarks>
    /// <para><strong>Critical Rule:</strong></para>
    /// <para>"Before asking for ANY information from the user, ALWAYS attempt to retrieve it from context using GetContextVariable.
    /// If the tool returns a valid value, USE IT without asking the user. Ask the user ONLY if the tool indicates the information is missing."</para>
    /// <para><strong>Return Values:</strong></para>
    /// <list type="bullet">
    /// <item><term>Variable found (HIT)</term><description>Returns the variable value directly</description></item>
    /// <item><term>Variable not found (MISS)</term><description>Returns instructional message for LLM</description></item>
    /// </list>
    /// <para><strong>Example LLM Interaction:</strong></para>
    /// <code>
    /// LLM: "I need the userId to fetch invoices. Let me check context first."
    /// Tool call: GetContextVariable("userId")
    /// Response: "P994E" (HIT)
    /// LLM: "Great! I have the userId. Proceeding to fetch invoices..."
    /// 
    /// vs.
    /// 
    /// LLM: "I need the userId to fetch invoices. Let me check context first."
    /// Tool call: GetContextVariable("userId")
    /// Response: "Information userId not available in context: you need to engage SetContextVariable to set it." (MISS)
    /// LLM: "Could you please provide your customer ID? #INT#"
    /// </code>
    /// <para><strong>Logging:</strong></para>
    /// <para>Logs HIT/MISS status with variable name and value for debugging context management issues.</para>
    /// </remarks>
    public Task<object> GetContextVariable(string variableName)
    {
        MorganaContextProvider provider = getContextProvider();
        object? value = provider.GetVariable(variableName);

        if (value != null)
        {
            toolLogger.LogInformation(
                $"MorganaTool ({GetType().Name}) HIT variable '{variableName}' from agent context. Value is: {value}");

            return Task.FromResult(value);
        }

        toolLogger.LogInformation(
            $"MorganaTool ({GetType().Name}) MISS variable '{variableName}' from agent context.");

        return Task.FromResult<object>(
            $"Information {variableName} not available in context: you need to engage SetContextVariable to set it.");
    }

    /// <summary>
    /// Sets a context variable in the agent's conversation context.
    /// If the variable is marked as shared (in configuration), the provider automatically broadcasts it to other agents.
    /// </summary>
    /// <param name="variableName">Name of the context variable to set (e.g., "userId", "invoiceId")</param>
    /// <param name="variableValue">Value to store in the context</param>
    /// <returns>Task containing a confirmation message for the LLM</returns>
    /// <remarks>
    /// <para><strong>Shared vs Local Variables:</strong></para>
    /// <para>Whether a variable is shared or local is determined by tool configuration in agents.json.
    /// Tools declare parameters with "Scope": "context" and "Shared": true/false.</para>
    /// <para><strong>Shared Variable Flow (example: userId):</strong></para>
    /// <list type="number">
    /// <item>BillingAgent calls SetContextVariable("userId", "P994E")</item>
    /// <item>MorganaContextProvider.SetVariable checks configuration: userId is marked Shared=true</item>
    /// <item>Provider calls agent's OnSharedContextUpdate callback</item>
    /// <item>Agent broadcasts to RouterActor via BroadcastContextUpdate</item>
    /// <item>RouterActor sends ReceiveContextUpdate to all other agents</item>
    /// <item>ContractAgent and TroubleshootingAgent receive and merge the userId</item>
    /// <item>All agents can now use userId without asking the user again</item>
    /// </list>
    /// <para><strong>Local Variable Flow (example: invoiceId):</strong></para>
    /// <list type="number">
    /// <item>BillingAgent calls SetContextVariable("invoiceId", "INV-2024-001")</item>
    /// <item>MorganaContextProvider.SetVariable checks configuration: invoiceId is marked Shared=false</item>
    /// <item>Variable stored locally, no broadcast occurs</item>
    /// <item>Only BillingAgent can access this variable</item>
    /// </list>
    /// <para><strong>LLM Feedback:</strong></para>
    /// <para>Returns a confirmation message that the LLM sees, confirming the variable was stored.
    /// This helps the LLM understand that the information is now available for future tool calls.</para>
    /// </remarks>
    public Task<object> SetContextVariable(string variableName, string variableValue)
    {
        MorganaContextProvider provider = getContextProvider();
        provider.SetVariable(variableName, variableValue);

        return Task.FromResult<object>(
            $"Information {variableName} inserted in context with value: {variableValue}");
    }
}