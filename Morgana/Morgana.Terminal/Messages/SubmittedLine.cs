using Morgana.Contracts;

namespace Morgana.Terminal.Messages;

/// <summary>
/// What Enter took off the prompt: the line and what it was resolved to while the palette's highlight still
/// belonged to it, so the line can be cleared before anything is awaited.
/// </summary>
/// <param name="Text">The line as typed, or the value given to the option the form was asking for.</param>
/// <param name="Command">The command the palette resolved the line to; null when the line is no command line or names none.</param>
/// <param name="WasFillingIn">True when the line answered the option a command form was asking for rather than opening a turn or a command.</param>
/// <param name="FormInvocation">The command the form has just gathered every value for; null while the form is still asking.</param>
public sealed record SubmittedLine(
    string Text,
    CommandDescriptor? Command,
    bool WasFillingIn,
    CommandInvocation? FormInvocation);
