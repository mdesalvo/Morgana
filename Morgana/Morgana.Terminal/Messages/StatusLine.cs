namespace Morgana.Terminal.Messages;

/// <summary>One line of a field of the <c>/status</c> panel: what it says and the Spectre style it is drawn in.</summary>
/// <param name="Text">The line as the user reads it, wrapped under the field's indent when it is long.</param>
/// <param name="Style">The Spectre style of the line, <c>default</c> for plain text.</param>
public sealed record StatusLine(string Text, string Style);
