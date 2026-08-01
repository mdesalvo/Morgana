namespace Morgana.AI.Attributes;

/// <summary>
/// Marks a MorganaTool class as providing native tools for a specific intent.
/// Used by MorganaAgentAdapter and IToolRegistryService to discover and instantiate tools at runtime.
/// </summary>
/// <remarks>
/// Enables automatic tool discovery for intents. When MorganaAgentAdapter creates an agent
/// for an intent, it queries IToolRegistryService to find the tool class decorated with this
/// attribute for that intent. Intent name must match [HandlesIntent] on corresponding agent
/// and agents.json Name. Tool class must inherit from MorganaTool and have matching public
/// methods for each tool defined in agents.json.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public class ProvidesToolForIntentAttribute : Attribute
{
    /// <summary>
    /// Gets the intent that this tool provides functionality for.
    /// Must match the intent specified in HandlesIntentAttribute on the corresponding agent.
    /// </summary>
    /// <value>
    /// Intent name (e.g., "billing", "contract", "monkeys")
    /// </value>
    /// <remarks>
    /// Must match [HandlesIntent] on agent class and agents.json Name/ID (case-sensitive).
    /// </remarks>
    public string Intent { get; }

    /// <summary>
    /// Initializes a new instance of the ProvidesToolForIntentAttribute.
    /// </summary>
    /// <param name="intent">Name of the intent this tool provides functionality for</param>
    /// <exception cref="ArgumentException">Thrown if intent is null, empty, or whitespace</exception>
    /// <remarks>
    /// Validates intent is not null/empty (compile-time error prevention).
    /// Use lowercase, single words or hyphens. Case-sensitive.
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