namespace Morgana.AI.Attributes;

/// <summary>Declares where a tool parameter's value comes from: the user, this agent's context or the context every agent of the conversation shares.</summary>
/// <remarks>A parameter whose value the model itself authors carries none.</remarks>
[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
public sealed class ToolParameterAttribute : Attribute
{
    /// <summary>Where the parameter's value comes from.</summary>
    public Records.ToolScope Scope { get; }

    /// <summary>Declares the parameter's scope.</summary>
    /// <param name="scope">Where the parameter's value comes from.</param>
    public ToolParameterAttribute(Records.ToolScope scope)
    {
        // Fixed by the declaration so that the projection reads it back unchanged.
        Scope = scope;
    }
}