namespace Morgana.AI.Attributes;

/// <summary>
/// Offers a tool method or a workflow class to the user as a button of its agent's menu.
/// </summary>
/// <remarks>
/// A tool without it is LLM-facing and never in a menu; the menu is written by code and the model only gives it a voice.
/// </remarks>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class OfferedToUserAttribute : Attribute
{
    /// <summary>Position of the button in the agent's menu; starts at 1 and is unique per agent across tools and workflows.</summary>
    public int Ordinal { get; }

    /// <summary>The text of the button and the message that its press sends.</summary>
    public string Label { get; }

    /// <summary>Declares the button that offers the tool or the workflow.</summary>
    /// <param name="ordinal">Position in the menu; at least 1.</param>
    /// <param name="label">Text of the button; not blank.</param>
    public OfferedToUserAttribute(int ordinal, string label)
    {
        // Both values are kept as declared: refusing a malformed one is the startup check's job.
        Ordinal = ordinal;
        Label = label;
    }
}