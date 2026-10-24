namespace Morgana.AI.Attributes;

/// <summary>
/// Declares a tool method or a workflow class as the user's: a button of its agent's menu.
/// </summary>
/// <remarks>
/// <para>Every tool method and workflow class carries this or <see cref="SystemToolAttribute"/>, never both, checked at startup; the menu is written by code and the model only gives it a voice.</para>
/// <para>A turn leaves off its menu the button of every tool that it called, since the user is looking at what it shows.
/// The tools that the model reads follow the order of the buttons, so the model describing what it does follows the menu.</para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class UserToolAttribute : Attribute
{
    /// <summary>Position of the button in the agent's menu; starts at 1 and is unique per agent across tools and workflows.</summary>
    public int Ordinal { get; }

    /// <summary>The text of the button and the message that its press sends.</summary>
    public string Label { get; }

    /// <summary>Declares the button that offers the tool or the workflow.</summary>
    /// <param name="ordinal">Position in the menu; at least 1.</param>
    /// <param name="label">Text of the button; not blank.</param>
    public UserToolAttribute(int ordinal, string label)
    {
        // Both values are kept as declared: refusing a malformed one is the startup check's job.
        Ordinal = ordinal;
        Label = label;
    }
}