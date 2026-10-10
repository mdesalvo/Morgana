namespace Morgana.AI.Attributes;

/// <summary>
/// Marks a MorganaWorkflow class as a workflow of one intent's agent.
/// </summary>
/// <remarks>
/// Intent name must match [HandlesIntent] on the corresponding agent. Several classes may name the same
/// intent: each one is one workflow of that agent. The class must have a public parameterless constructor.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public class ProvidesWorkflowForIntentAttribute : Attribute
{
    /// <summary>
    /// Gets the intent whose agent runs the workflow.
    /// </summary>
    /// <value>
    /// Intent name (e.g., "inventory")
    /// </value>
    /// <remarks>
    /// Must match [HandlesIntent] on the agent class (case-insensitive at discovery).
    /// </remarks>
    public string Intent { get; }

    /// <summary>
    /// Initializes a new instance of the ProvidesWorkflowForIntentAttribute.
    /// </summary>
    /// <param name="intent">Name of the intent whose agent runs the workflow</param>
    /// <exception cref="ArgumentException">Thrown if intent is null, empty or whitespace</exception>
    public ProvidesWorkflowForIntentAttribute(string intent)
    {
        if (string.IsNullOrWhiteSpace(intent))
            throw new ArgumentException("Intent cannot be null or empty", nameof(intent));

        // The intent is the key that attaches this class's workflow to its agent.
        Intent = intent;
    }
}