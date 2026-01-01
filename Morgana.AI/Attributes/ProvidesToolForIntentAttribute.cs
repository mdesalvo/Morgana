namespace Morgana.AI.Attributes;

/// <summary>
/// Marks a MorganaTool class as providing native tools for a specific intent.
/// Used by AgentAdapter to discover and instantiate native tools at runtime.
/// </summary>
/// <example>
/// [ProvidesToolForIntent("billing")]
/// public class BillingTool : MorganaTool
/// {
///     // Tool implementation
/// }
/// </example>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public class ProvidesToolForIntentAttribute : Attribute
{
    /// <summary>
    /// The intent that this tool provides functionality for.
    /// Must match the intent specified in HandlesIntentAttribute on the corresponding agent.
    /// </summary>
    public string Intent { get; }
    
    public ProvidesToolForIntentAttribute(string intent)
    {
        if (string.IsNullOrWhiteSpace(intent))
        {
            throw new ArgumentException("Intent cannot be null or empty", nameof(intent));
        }
        
        Intent = intent;
    }
}