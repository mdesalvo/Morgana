namespace Morgana.AI.Attributes;

/// <summary>
/// Marks a MorganaTool class as providing native tools for a specific intent.
/// Used by MorganaAgentAdapter and IToolRegistryService to discover and instantiate tools at runtime.
/// </summary>
/// <remarks>
/// <para><strong>Purpose:</strong></para>
/// <para>This attribute enables automatic discovery of tool implementations for specific intents.
/// When an agent is created for an intent, the MorganaAgentAdapter queries the IToolRegistryService
/// to find the tool class decorated with [ProvidesToolForIntent] for that intent.</para>
/// <para><strong>Tool Discovery Flow:</strong></para>
/// <code>
/// 1. MorganaAgentAdapter creates agent for "billing" intent
/// 2. Queries IToolRegistryService.FindToolTypeForIntent("billing")
/// 3. Registry scans assemblies for [ProvidesToolForIntent("billing")]
/// 4. Finds BillingTool and returns Type
/// 5. MorganaAgentAdapter instantiates BillingTool via Activator.CreateInstance
/// 6. Registers tool methods in MorganaToolAdapter
/// 7. Tools become available to agent during LLM interactions
/// </code>
/// <para><strong>Intent Coordination:</strong></para>
/// <para>The intent specified here must match the intent handled by the corresponding agent:</para>
/// <code>
/// [HandlesIntent("billing")]      // Agent
/// public class BillingAgent : MorganaAgent { }
///
/// [ProvidesToolForIntent("billing")]  // Tool (must match)
/// public class BillingTool : MorganaTool { }
/// </code>
/// <para><strong>Tool Method Registration:</strong></para>
/// <para>Tool classes contain public methods that match tool definitions in agents.json.
/// The MorganaAgentAdapter uses reflection to find and register these methods as callable tools.</para>
/// <para><strong>Restrictions:</strong></para>
/// <list type="bullet">
/// <item>Only one tool class can provide tools for a specific intent</item>
/// <item>Cannot be applied multiple times to the same class</item>
/// <item>Cannot be inherited by derived classes</item>
/// <item>Can only be applied to classes (not methods or properties)</item>
/// <item>Tool class must inherit from MorganaTool</item>
/// <item>Tool class must have constructor: (ILogger, Func&lt;MorganaContextProvider&gt;)</item>
/// </list>
/// </remarks>
/// <example>
/// <para><strong>Complete tool class with ProvidesToolForIntent:</strong></para>
/// <code>
/// [ProvidesToolForIntent("billing")]
/// public class BillingTool : MorganaTool
/// {
///     public BillingTool(
///         ILogger&lt;BillingTool&gt; toolLogger,
///         Func&lt;MorganaContextProvider&gt; getContextProvider)
///         : base(toolLogger, getContextProvider)
///     {
///     }
///
///     // Tool methods matching definitions in agents.json
///
///     public async Task&lt;InvoiceList&gt; GetInvoices(string userId, int count)
///     {
///         // Fetch invoices from backend
///         return await FetchInvoicesFromBackend(userId.ToString(), count);
///     }
///
///     public async Task&lt;InvoiceDetails&gt; GetInvoiceDetails(string invoiceId)
///     {
///         // Fetch invoice details
///         return await FetchInvoiceDetailsFromBackend(invoiceId);
///     }
/// }
/// </code>
/// <para><strong>Corresponding agents.json configuration:</strong></para>
/// <code>
/// {
///   "Intents": [
///   {
///       "Name": "billing",
///       "Description": "requests to view the list of invoices, extract or explain detail items of a specific user invoice",
///       "Label": "📄 Billing",
///       "DefaultValue": "I would like to check my invoices"
///   },
///   "Agents": [
///     {
///       "ID": "Billing",
///       "Type": "INTENT",
///       "SubType": "AGENT",
///       "Content": "...",
///       "Instructions": "...",
///       "Personality": "...",
///       "Language": "en-US",
///       "AdditionalProperties": [
///         {
///           "Tools": [
///             {
///               "Name": "GetInvoices",  // Must match method name
///               "Description": "Retrieves the last N invoices of the user",
///               "Parameters": [
///                 {
///                   "Name": "count",
///                   "Description": "Number of recent invoices to retrieve",
///                   "Required": true,
///                   "Scope": "request"
///                 }
///               ]
///             }
///           ]
///         }
///       ]
///     }
///   ]
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public class ProvidesToolForIntentAttribute : Attribute
{
    /// <summary>
    /// Gets the intent that this tool provides functionality for.
    /// Must match the intent specified in HandlesIntentAttribute on the corresponding agent.
    /// </summary>
    /// <value>
    /// Intent name (e.g., "billing", "contract", "troubleshooting")
    /// </value>
    /// <remarks>
    /// <para><strong>Intent Naming Consistency:</strong></para>
    /// <para>The intent name must exactly match (case-sensitive) the intent used in:</para>
    /// <list type="bullet">
    /// <item>The [HandlesIntent] attribute on the agent class</item>
    /// <item>The "Name" field in agents.json Intents array</item>
    /// <item>The "ID" field in agents.json Agents array (capitalized version)</item>
    /// </list>
    /// </remarks>
    public string Intent { get; }

    /// <summary>
    /// Initializes a new instance of the ProvidesToolForIntentAttribute.
    /// </summary>
    /// <param name="intent">Name of the intent this tool provides functionality for</param>
    /// <exception cref="ArgumentException">Thrown if intent is null, empty, or whitespace</exception>
    /// <remarks>
    /// <para><strong>Validation:</strong></para>
    /// <para>The constructor validates that the intent parameter is not null, empty, or whitespace.
    /// This prevents configuration errors at compile time rather than runtime.</para>
    /// <para><strong>Naming Conventions:</strong></para>
    /// <list type="bullet">
    /// <item>Use lowercase for intent names (e.g., "billing" not "Billing")</item>
    /// <item>Use single words or hyphens (e.g., "tech-support" not "tech support")</item>
    /// <item>Intent names are case-sensitive in tool discovery logic</item>
    /// </list>
    /// </remarks>
    public ProvidesToolForIntentAttribute(string intent)
    {
        if (string.IsNullOrWhiteSpace(intent))
        {
            throw new ArgumentException("Intent cannot be null or empty", nameof(intent));
        }

        Intent = intent;
    }
}