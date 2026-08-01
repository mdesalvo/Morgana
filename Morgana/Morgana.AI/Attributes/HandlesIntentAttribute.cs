namespace Morgana.AI.Attributes;

/// <summary>
/// Marks a MorganaAgent class as the handler for a specific intent.
/// Used by MorganaAgentAdapter and RouterActor to discover and route requests to the appropriate agent.
/// </summary>
/// <remarks>
/// Establishes mapping between intent names (from classification) and agent implementations.
/// RouterActor uses this to discover which agent should handle each intent at runtime.
/// Intent name must match corresponding entry in agents.json. Only one agent can handle
/// a specific intent.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public class HandlesIntentAttribute : Attribute
{
    /// <summary>
    /// Gets the intent name that this agent handles.
    /// Must match the intent name in agents.json configuration.
    /// </summary>
    /// <value>
    /// Intent name (e.g., "billing", "contract", "monkeys")
    /// </value>
    public string Intent { get; }

    /// <summary>
    /// Initializes a new instance of the HandlesIntentAttribute.
    /// </summary>
    /// <param name="intent">Name of the intent this agent handles (must match agents.json)</param>
    /// <remarks>
    /// Intent name must match agents.json definition. Use lowercase, single words or hyphens.
    /// Case-sensitive in routing logic.
    /// </remarks>
    public HandlesIntentAttribute(string intent)
    {
        Intent = intent;
    }
}