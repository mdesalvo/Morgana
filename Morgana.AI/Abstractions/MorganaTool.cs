using System.Text.Json;
using Microsoft.Extensions.Logging;
using Morgana.AI.Providers;

namespace Morgana.AI.Abstractions;

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
///   ├── GetInvoices (domain-specific tool)
///   ├── GetContractDetails (domain-specific tool)
///   ├── RunDiagnostics (domain-specific tool)
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
/// public class GetInvoicesTool : MorganaTool
/// {
///     [ProvideToolForIntent("billing")]
///     public async Task&lt;InvoiceList&gt; GetInvoices(int count)
///     {
///         // First, try to get userId from context
///         object userId = await GetContextVariable("userId");
///
///         // If missing, prompt LLM to ask user
///         // If present, fetch invoices from backend
///
///         return invoices;
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
    /// Direct access to the context provider instance. Lazily evaluated from getContextProvider.
    /// Used for quick reply storage and retrieval via special context key "__pending_quick_replies".
    /// </summary>
    protected MorganaContextProvider contextProvider => getContextProvider();

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
    /// <para><strong>Critical Rule (from SKILL.md):</strong></para>
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

    // =========================================================================
    // QUICK REPLY SYSTEM TOOL
    // =========================================================================

    /// <summary>
    /// System tool that allows the LLM to set quick reply buttons for the user interface.
    /// This tool gives the LLM control over when and how to offer guided interaction options.
    /// </summary>
    /// <param name="quickReplies">
    /// JSON string containing an array of quick reply definitions.
    /// Each quick reply has: id (identifier), label (display text with emoji), value (message to send).
    /// </param>
    /// <returns>Confirmation message for the LLM indicating quick replies were set</returns>
    /// <remarks>
    /// <para><strong>Purpose:</strong></para>
    /// <para>SetQuickReplies is a SYSTEM TOOL that the LLM can call when it wants to provide
    /// guided interaction options to the user. This gives the LLM full control over quick reply
    /// generation based on conversation context.</para>
    ///
    /// <para><strong>LLM Decision Making:</strong></para>
    /// <para>The LLM decides to call SetQuickReplies when:</para>
    /// <list type="bullet">
    /// <item>Presenting multiple options to choose from (guides, invoices, actions)</item>
    /// <item>Asking a question with predefined answers (yes/no, selections)</item>
    /// <item>Offering next steps after completing an operation</item>
    /// <item>Providing navigation options in a multi-step workflow</item>
    /// </list>
    ///
    /// <para><strong>JSON Format Expected:</strong></para>
    /// <code>
    /// [
    ///   {
    ///     "id": "no-internet",
    ///     "label": "🔴 No Internet Connection",
    ///     "value": "Show me the no-internet troubleshooting guide"
    ///   },
    ///   {
    ///     "id": "slow-speed",
    ///     "label": "🐌 Slow Connection Speed",
    ///     "value": "Show me the slow-connection troubleshooting guide"
    ///   }
    /// ]
    /// </code>
    ///
    /// <para><strong>Single Record Design:</strong></para>
    /// <para>Uses QuickReply with JsonPropertyName attributes.
    /// Same record serves as both runtime model and JSON DTO - no duplication!</para>
    ///
    /// <para><strong>Design Guidelines for LLM:</strong></para>
    /// <list type="bullet">
    /// <item><term>Limit to 3-5 options</term><description>UI constraint, too many buttons are overwhelming</description></item>
    /// <item><term>Use emoji in labels</term><description>Visual appeal: "🔧 Run Diagnostics" not "Run Diagnostics"</description></item>
    /// <item><term>Action-oriented labels</term><description>"Show Invoice Details" not "Invoice Details"</description></item>
    /// <item><term>Natural values</term><description>Value should be what user would naturally type</description></item>
    /// <item><term>Clear IDs</term><description>Use descriptive IDs like "invoice-001" not "btn1"</description></item>
    /// </list>
    /// </remarks>
    public Task<object> SetQuickReplies(string quickReplies)
    {
        try
        {
            // Validate JSON by attempting to parse
            List<Records.QuickReply>? parsedQuickReplies = JsonSerializer.Deserialize<List<Records.QuickReply>>(quickReplies);
            if (parsedQuickReplies == null || !parsedQuickReplies.Any())
            {
                toolLogger.LogWarning("SetQuickReplies called with empty or invalid JSON");
                return Task.FromResult<object>("Warning: No quick replies were set (empty or invalid data).");
            }

            // Store the JSON string of the quick replies under a reserved context variable
            contextProvider.SetVariable("__pending_quick_replies", quickReplies);
            toolLogger.LogInformation($"LLM set {parsedQuickReplies.Count} quick reply buttons via SetQuickReplies tool");

            // Return confirmation to LLM
            return Task.FromResult<object>(
                $"Quick reply buttons set successfully. The user will see {parsedQuickReplies.Count} interactive options. " +
                $"Now provide your text response to the user - the quick reply buttons will appear below your message.");
        }
        catch (JsonException ex)
        {
            toolLogger.LogError(ex, "Failed to parse quick replies JSON in SetQuickReplies");
            return Task.FromResult<object>(
                "Error: Quick replies JSON format is invalid. Expected format: " +
                "[{\"id\": \"option1\", \"label\": \"🔧 Option 1\", \"value\": \"User message for option 1\"}]");
        }
    }
}