namespace Morgana.AI.Attributes;

/// <summary>
/// Declares on a tool method whether it runs only once the user has approved that exact call.
/// </summary>
/// <remarks>
/// Mandatory on every tool method of a <c>MorganaTool</c> subclass: startup refuses a tool without it.
/// Declare <c>true</c> for a tool that changes something real; the approval itself is Microsoft.Extensions.AI's,
/// through <c>ApprovalRequiredAIFunction</c>.
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class RequiresApprovalAttribute : Attribute
{
    /// <summary>True when the tool runs only on a call that the user has approved.</summary>
    public bool Required { get; }

    /// <summary>Declares whether the tool requires the user's approval.</summary>
    /// <param name="required">True when the tool changes something real.</param>
    public RequiresApprovalAttribute(bool required)
    {
        // The flag is what makes the framework hold the call until the user has approved it.
        Required = required;
    }
}
