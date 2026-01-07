namespace Morgana.AI.Interfaces;

/// <summary>
/// Service for runtime discovery and resolution of agent types based on intent classification.
/// Provides the mapping between intent names and agent implementation types via [HandlesIntent] attribute scanning.
/// </summary>
/// <remarks>
/// <para><strong>Purpose:</strong></para>
/// <para>This service enables dynamic agent discovery without hardcoded mappings. It scans loaded assemblies
/// (including plugins) for classes decorated with [HandlesIntent] attribute and builds a runtime registry.</para>
/// <para><strong>Agent Discovery Flow:</strong></para>
/// <code>
/// 1. Application startup
/// 2. PluginLoaderService loads domain assemblies
/// 3. IAgentRegistryService scans all loaded assemblies
/// 4. Finds classes with [HandlesIntent] attribute
/// 5. Builds intent → Type mapping dictionary
/// 6. RouterActor queries registry to discover agents
/// 7. Creates agent instances via ActorSystem.GetOrCreateAgent
/// </code>
/// <para><strong>Integration with RouterActor:</strong></para>
/// <code>
/// // RouterActor constructor
/// foreach (string intent in agentResolverService.GetAllIntents())
/// {
///     Type? agentType = agentResolverService.ResolveAgentFromIntent(intent);
///     if (agentType != null)
///     {
///         agents[intent] = await Context.System.GetOrCreateAgent(
///             agentType,
///             intent,
///             conversationId
///         );
///     }
/// }
/// </code>
/// <para><strong>Plugin Support:</strong></para>
/// <para>This design enables plugin agents to be discovered and registered automatically without
/// modifying the core Morgana framework. Simply adding a plugin assembly with [HandlesIntent] agents
/// makes them available for routing.</para>
/// <para><strong>Default Implementation:</strong></para>
/// <para>HandlesIntentAgentRegistryService scans AppDomain.CurrentDomain.GetAssemblies() at startup
/// to build the registry. Alternative implementations could use lazy loading or dynamic assembly loading.</para>
/// </remarks>
public interface IAgentRegistryService
{
    /// <summary>
    /// Resolves the agent type that handles a specific intent.
    /// Uses [HandlesIntent] attribute to find the matching agent class.
    /// </summary>
    /// <param name="intent">Intent name from classification (e.g., "billing", "contract")</param>
    /// <returns>
    /// Type of the agent class decorated with [HandlesIntent(intent)], or null if no agent handles this intent.
    /// </returns>
    /// <remarks>
    /// <para><strong>Resolution Logic:</strong></para>
    /// <list type="number">
    /// <item>Normalize intent (typically lowercase)</item>
    /// <item>Look up in internal intent → Type dictionary</item>
    /// <item>Return Type if found, null otherwise</item>
    /// </list>
    /// <para><strong>Usage Example:</strong></para>
    /// <code>
    /// Type? agentType = agentRegistryService.ResolveAgentFromIntent("billing");
    ///
    /// if (agentType != null)
    /// {
    ///     // Found: typeof(BillingAgent) decorated with [HandlesIntent("billing")]
    ///     IActorRef agent = await Context.System.GetOrCreateAgent(
    ///         agentType,
    ///         "billing",
    ///         conversationId
    ///     );
    /// }
    /// else
    /// {
    ///     // No agent registered for "billing" intent
    ///     // Return error message to user
    /// }
    /// </code>
    /// <para><strong>Case Sensitivity:</strong></para>
    /// <para>Intent matching is typically case-insensitive in implementations, but the exact behavior
    /// depends on the implementation. Best practice is to use lowercase intent names consistently.</para>
    /// <para><strong>Null Handling:</strong></para>
    /// <para>Returning null (rather than throwing) allows RouterActor to provide user-friendly error messages
    /// when an unrecognized intent is classified (e.g., "I'm sorry, I don't know the magic formula for this request").</para>
    /// </remarks>
    Type? ResolveAgentFromIntent(string intent);

    /// <summary>
    /// Gets all registered intent names from discovered agents.
    /// Returns the set of intents that have agent implementations available.
    /// </summary>
    /// <returns>
    /// Enumerable of intent names that have corresponding [HandlesIntent] agents registered.
    /// </returns>
    /// <remarks>
    /// <para><strong>Usage Scenarios:</strong></para>
    /// <list type="bullet">
    /// <item><term>RouterActor initialization</term><description>Pre-create all agents for known intents</description></item>
    /// <item><term>Validation</term><description>Verify configuration intents have implementations</description></item>
    /// <item><term>Diagnostics</term><description>Log available agents at startup</description></item>
    /// <item><term>Admin UI</term><description>Display available conversation capabilities</description></item>
    /// </list>
    /// <para><strong>Example:</strong></para>
    /// <code>
    /// // Log available agents at startup
    /// IEnumerable&lt;string&gt; intents = agentRegistryService.GetAllIntents();
    /// logger.LogInformation($"Registered agents for intents: {string.Join(", ", intents)}");
    /// // Output: "Registered agents for intents: billing, contract, troubleshooting"
    ///
    /// // RouterActor: Create agent for each registered intent
    /// foreach (string intent in agentRegistryService.GetAllIntents())
    /// {
    ///     Type? agentType = agentRegistryService.ResolveAgentFromIntent(intent);
    ///     agents[intent] = await Context.System.GetOrCreateAgent(agentType, intent, conversationId);
    /// }
    /// </code>
    /// <para><strong>Configuration Mismatch Detection:</strong></para>
    /// <para>Compare GetAllIntents() results with intents defined in agents.json to detect configuration issues
    /// (intents in config but no agent implementation, or vice versa).</para>
    /// </remarks>
    IEnumerable<string> GetAllIntents();
}