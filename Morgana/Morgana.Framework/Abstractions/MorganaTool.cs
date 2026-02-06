using Microsoft.Extensions.Logging;
using Morgana.Framework.Providers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Morgana.Framework.Abstractions;

/// <summary>
/// Base class for agent tools that provides context variable access (Get/Set operations).
/// Tools can read from and write to the agent's conversation context via MorganaAIContextProvider.
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
    /// Factory function to retrieve the current MorganaAIContextProvider instance.
    /// Uses a function to enable lazy evaluation and ensure correct scoping per request.
    /// </summary>
    protected readonly Func<MorganaAIContextProvider> getAIContextProvider;

    /// <summary>
    /// Initializes a new instance of MorganaTool with logging and AI context provider access.
    /// </summary>
    /// <param name="toolLogger">Logger instance for tool diagnostics</param>
    /// <param name="getAIContextProvider">Factory function to retrieve the AI context provider</param>
    /// <remarks>
    /// The AI context provider is passed as a Func to ensure each tool call gets the correct
    /// scoped provider instance for the current conversation and agent.
    /// </remarks>
    public MorganaTool(
        ILogger toolLogger,
        Func<MorganaAIContextProvider> getAIContextProvider)
    {
        this.toolLogger = toolLogger;
        this.getAIContextProvider = getAIContextProvider;
    }

    // =========================================================================
    // CONTEXT SYSTEM TOOLS
    // =========================================================================

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
        MorganaAIContextProvider aiContextProvider = getAIContextProvider();
        object? value = aiContextProvider.GetVariable(variableName);

        if (value != null)
        {
            toolLogger.LogInformation(
                $"{nameof(MorganaTool)} ({GetType().Name}) HIT variable '{variableName}' from agent context. Value is: {value}");

            return Task.FromResult(value);
        }

        toolLogger.LogInformation(
            $"{nameof(MorganaTool)} ({GetType().Name}) MISS variable '{variableName}' from agent context.");

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
    /// <item>MorganaAIContextProvider.SetVariable checks configuration: userId is marked Shared=true</item>
    /// <item>Provider calls agent's OnSharedContextUpdate callback</item>
    /// <item>Agent broadcasts to RouterActor via BroadcastContextUpdate</item>
    /// <item>RouterActor sends ReceiveContextUpdate to all other agents</item>
    /// <item>ContractAgent and MonkeysAgent receive and merge the userId</item>
    /// <item>All agents can now use userId without asking the user again</item>
    /// </list>
    /// <para><strong>Local Variable Flow (example: invoiceId):</strong></para>
    /// <list type="number">
    /// <item>BillingAgent calls SetContextVariable("invoiceId", "INV-2024-001")</item>
    /// <item>MorganaAIContextProvider.SetVariable checks configuration: invoiceId is marked Shared=false</item>
    /// <item>Variable stored locally, no broadcast occurs</item>
    /// <item>Only BillingAgent can access this variable</item>
    /// </list>
    /// <para><strong>LLM Feedback:</strong></para>
    /// <para>Returns a confirmation message that the LLM sees, confirming the variable was stored.
    /// This helps the LLM understand that the information is now available for future tool calls.</para>
    /// </remarks>
    public Task<object> SetContextVariable(string variableName, string variableValue)
    {
        MorganaAIContextProvider aiContextProvider = getAIContextProvider();
        aiContextProvider.SetVariable(variableName, variableValue);

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
    ///     "value": "Show me the no-internet assistance guide"
    ///   },
    ///   {
    ///     "id": "slow-speed",
    ///     "label": "🐌 Slow Connection Speed",
    ///     "value": "Show me the slow-connection assistance guide"
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
            MorganaAIContextProvider aiContextProvider = getAIContextProvider();
            aiContextProvider.SetVariable("quick_replies", quickReplies);

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

    // =========================================================================
    // RICH CARD SYSTEM TOOL
    // =========================================================================

    /// <summary>
    /// Sets a rich card for structured visual presentation of complex data.
    /// LLM calls this tool when presenting invoices, profiles, reports, or any structured information
    /// that benefits from visual hierarchy instead of plain text.
    /// </summary>
    /// <param name="richCard">
    /// JSON string containing the rich card structure with title, subtitle, and components array.
    /// </param>
    /// <returns>Confirmation message for the LLM indicating the card was set</returns>
    /// <remarks>
    /// <para><strong>Purpose:</strong></para>
    /// <para>SetRichCard is a SYSTEM TOOL enabling the LLM to present structured data visually.
    /// Similar to SetQuickReplies but for data presentation rather than user actions.</para>
    ///
    /// <para><strong>LLM Decision Making:</strong></para>
    /// <para>The LLM should call SetRichCard when presenting:</para>
    /// <list type="bullet">
    /// <item>Invoices, receipts, financial documents</item>
    /// <item>User or product profiles</item>
    /// <item>Structured reports with sections</item>
    /// <item>Comparisons or side-by-side data</item>
    /// <item>Any data that benefits from visual organization</item>
    /// </list>
    ///
    /// <para><strong>JSON Format Expected:</strong></para>
    /// <code>
    /// {
    ///   "title": "Invoice #2024-001",
    ///   "subtitle": "Issued on 15/01/2024",
    ///   "components": [
    ///     { "type": "key_value", "key": "Customer", "value": "Acme Corp" },
    ///     { "type": "divider" },
    ///     { "type": "section", "title": "Line Items", "components": [
    ///       { "type": "list", "items": ["Consulting: €800", "Development: €450"], "style": "plain" }
    ///     ]},
    ///     { "type": "key_value", "key": "Total", "value": "€1,250.00", "emphasize": true },
    ///     { "type": "badge", "text": "Paid", "variant": "success" }
    ///   ]
    /// }
    /// </code>
    ///
    /// <para><strong>Constraints:</strong></para>
    /// <list type="bullet">
    /// <item>Maximum nesting depth: 3 levels (validated before storage)</item>
    /// <item>Maximum 50 components total (prevents abuse)</item>
    /// <item>Keep cards focused: 10-20 components recommended</item>
    /// </list>
    /// </remarks>
    public Task<object> SetRichCard(string richCard)
    {
        try
        {
            // Validate JSON by attempting to deserialize
            Records.RichCard? parsedRichCard = JsonSerializer.Deserialize<Records.RichCard>(
                richCard, new JsonSerializerOptions
                {
                    AllowOutOfOrderMetadataProperties = true,
                    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
                    PropertyNameCaseInsensitive = true
                }
            );
            if (parsedRichCard == null)
            {
                toolLogger.LogWarning("SetRichCard called with invalid JSON structure");
                return Task.FromResult<object>("Error: Rich card JSON structure is invalid.");
            }

            // Validate depth constraint (max 3 levels)
            int depth = CalculateMaxDepth(parsedRichCard.Components, 1);
            if (depth > 3)
            {
                toolLogger.LogWarning($"SetRichCard called with excessive nesting depth: {depth} (max 3)");
                return Task.FromResult<object>(
                    $"Error: Rich card exceeds maximum nesting depth of 3 (found: {depth}). " +
                    $"Please simplify the card structure.");
            }

            // Validate component count (max 50 to prevent abuse)
            int totalComponents = CountComponents(parsedRichCard.Components);
            if (totalComponents > 50)
            {
                toolLogger.LogWarning($"SetRichCard called with too many components: {totalComponents} (max 50)");
                return Task.FromResult<object>(
                    $"Error: Rich card has too many components: {totalComponents} (max 50). " +
                    $"Please create a more focused card.");
            }

            // Store the rich card JSON under a reserved context variable
            MorganaAIContextProvider aiContextProvider = getAIContextProvider();
            aiContextProvider.SetVariable("rich_card", richCard);

            toolLogger.LogInformation(
                $"LLM set rich card '{parsedRichCard.Title}' with {totalComponents} components " +
                $"(depth: {depth}) via SetRichCard tool");

            // Return confirmation to LLM
            return Task.FromResult<object>(
                $"Rich card set successfully. The user will see a structured visual card titled '{parsedRichCard.Title}'. " +
                $"You can now provide additional context or explanation in text if needed.");
        }
        catch (JsonException ex)
        {
            toolLogger.LogError(ex, "Failed to parse rich card JSON in SetRichCard");
            return Task.FromResult<object>(
                "Error: Rich card JSON format is invalid. Please check the structure and try again.");
        }
    }

    /// <summary>
    /// Calculates the maximum nesting depth of components in a card.
    /// Used by SetRichCard to enforce 3-level depth constraint.
    /// </summary>
    /// <param name="components">List of card components to analyze</param>
    /// <param name="currentDepth">Current depth level (starts at 1)</param>
    /// <returns>Maximum depth found in the component tree</returns>
    private int CalculateMaxDepth(List<Records.CardComponent> components, int currentDepth)
    {
        int maxDepth = currentDepth;

        foreach (Records.CardComponent component in components)
        {
            if (component is Records.SectionComponent section)
            {
                int sectionDepth = CalculateMaxDepth(section.Components, currentDepth + 1);
                maxDepth = Math.Max(maxDepth, sectionDepth);
            }
        }

        return maxDepth;
    }

    /// <summary>
    /// Counts total number of components recursively (including nested sections).
    /// Used by SetRichCard to enforce 50-component limit.
    /// </summary>
    /// <param name="components">List of card components to count</param>
    /// <returns>Total component count including all nested components</returns>
    private int CountComponents(List<Records.CardComponent> components)
    {
        int count = components.Count;

        foreach (Records.CardComponent component in components)
        {
            if (component is Records.SectionComponent section)
            {
                count += CountComponents(section.Components);
            }
        }

        return count;
    }
}