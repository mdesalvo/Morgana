namespace Morgana.AI.Attributes;

/// <summary>
/// Declares on a tool method that it runs only once the user has approved that exact call.
/// </summary>
/// <remarks>
/// Declared on a tool that changes something real; a tool without it runs as soon as the model calls it.
/// The approval itself is Microsoft.Extensions.AI's, through <c>ApprovalRequiredAIFunction</c>.
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class RequiresApprovalAttribute : Attribute;