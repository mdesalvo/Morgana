namespace Morgana.AI.Attributes;

/// <summary>
/// Marks a MorganaAgent class as the handler for a specific intent.
/// Used by MorganaAgentAdapter and RouterActor to discover and route requests to the appropriate agent.
/// </summary>
/// <remarks>
/// <para><strong>Purpose:</strong></para>
/// <para>This attribute establishes the mapping between intent names (from classification) and agent implementations.
/// The RouterActor uses this attribute to discover which agent should handle each intent at runtime.</para>
/// <para><strong>Intent Routing Flow:</strong></para>
/// <code>
/// 1. User message classified as "billing" intent
/// 2. RouterActor queries IAgentRegistryService for agent handling "billing"
/// 3. Registry scans assemblies for classes decorated with [HandlesIntent("billing")]
/// 4. Finds BillingAgent and routes request to it
/// </code>
/// <para><strong>Usage Example:</strong></para>
/// <code>
/// [HandlesIntent("billing")]
/// public class BillingAgent : MorganaAgent
/// {
///     public BillingAgent(...) : base(...)
///     {
///         // Agent initialization
///     }
/// }
/// </code>
/// <para><strong>Configuration Coordination:</strong></para>
/// <para>The intent name specified here must match the intent definition in agents.json:</para>
/// <code>
/// // agents.json
/// {
///   "Intents": [
///     {
///       "Name": "billing",  // Must match attribute
///       "Description": "requests to view invoices...",
///       "Label": "📄 Billing"
///     }
///   ]
/// }
/// </code>
/// <para><strong>Restrictions:</strong></para>
/// <list type="bullet">
/// <item>Only one agent can handle a specific intent</item>
/// <item>Cannot be applied multiple times to the same class</item>
/// <item>Can only be applied to classes (not methods or properties)</item>
/// </list>
/// </remarks>
/// <example>
/// <para>Complete agent declaration with HandlesIntent:</para>
/// <code>
/// [HandlesIntent("contract")]
/// public class ContractAgent : MorganaAgent
/// {
///     public ContractAgent(
///         string conversationId,
///         ILLMService llmService,
///         IPromptResolverService promptResolverService,
///         ILogger&lt;ContractAgent&gt; agentLogger,
///         MorganaAgentAdapter agentAdapter)
///         : base(conversationId, llmService, promptResolverService, agentLogger)
///     {
///         (aiAgent, contextProvider) = agentAdapter.CreateAgent(
///             GetType(),
///             OnSharedContextUpdate);
///
///         ReceiveAsync&lt;Records.AgentRequest&gt;(ExecuteAgentAsync);
///     }
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public class HandlesIntentAttribute : Attribute
{
    /// <summary>
    /// Gets the intent name that this agent handles.
    /// Must match the intent name in agents.json configuration.
    /// </summary>
    /// <value>
    /// Intent name (e.g., "billing", "contract", "troubleshooting")
    /// </value>
    public string Intent { get; }

    /// <summary>
    /// Initializes a new instance of the HandlesIntentAttribute.
    /// </summary>
    /// <param name="intent">Name of the intent this agent handles (must match agents.json)</param>
    /// <remarks>
    /// <para><strong>Naming Conventions:</strong></para>
    /// <list type="bullet">
    /// <item>Use lowercase for intent names (e.g., "billing" not "Billing")</item>
    /// <item>Use single words or hyphens (e.g., "tech-support" not "tech support")</item>
    /// <item>Intent names are case-sensitive in routing logic</item>
    /// </list>
    /// </remarks>
    public HandlesIntentAttribute(string intent)
    {
        Intent = intent;
    }
}