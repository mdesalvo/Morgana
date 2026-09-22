namespace Morgana.Contracts;

/// <summary>
/// A value a command is given when it is run, written at the prompt as <c>name:value</c>. What a command
/// accepts is declared once on its descriptor, so a channel can offer it, refuse what was never declared
/// and refuse a run missing what the command cannot work without.
/// </summary>
/// <param name="Name">The option as the user types it, lowercase and without colon: <c>path</c>, not <c>Path:</c>.</param>
/// <param name="Description">One line telling the user what the value is for.</param>
/// <param name="Required">True when the command cannot run without it, which is refused before anything happens.</param>
public record CommandOption(
    string Name,
    string Description,
    bool Required = false);