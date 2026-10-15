namespace Morgana.AI.Attributes;

/// <summary>
/// Declares a tool method or a workflow class as the system's: only the model calls it and it is never a button of a menu.
/// </summary>
/// <remarks>
/// Every tool method and workflow class carries this or <see cref="UserToolAttribute"/>, never both, checked at startup.
/// </remarks>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class SystemToolAttribute : Attribute;