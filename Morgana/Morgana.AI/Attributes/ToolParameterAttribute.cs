namespace Morgana.AI.Attributes;

/// <summary>
/// Declares where a tool parameter's value comes from and whether it is shared across agents.
/// </summary>
/// <remarks>
/// Mandatory on every parameter of a tool method, beside <c>[Description]</c> which is the prose that the model reads.
/// Whether the model must supply the value is never declared: it is the signature, so a parameter without a default is required.
/// Startup refuses a <c>Request</c> parameter that is shared, a <c>Context</c> parameter with a default and a <c>Context</c> parameter that is not a <c>string</c>.
/// </remarks>
[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
public sealed class ToolParameterAttribute : Attribute
{
    /// <summary>Where the parameter's value comes from.</summary>
    public Records.ToolScope Scope { get; }

    /// <summary>True when the value is written into the conversation's shared context for the other agents to read.</summary>
    public bool Shared { get; }

    /// <summary>Declares the parameter's scope and whether it is shared.</summary>
    /// <param name="scope">Context when the framework resolves the value from the session, Request when the user supplies it.</param>
    /// <param name="shared">True for a context value that every agent of the conversation reads.</param>
    public ToolParameterAttribute(Records.ToolScope scope, bool shared = false)
    {
        // The scope decides whether the value is held context or asked of the user; sharing routes it into the conversation's registry.
        Scope = scope;
        Shared = shared;
    }
}
