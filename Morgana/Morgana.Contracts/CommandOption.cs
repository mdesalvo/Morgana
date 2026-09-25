namespace Morgana.Contracts;

/// <summary>
/// A value a command is given when it is run, written at the prompt as <c>name:value</c>. What a command
/// accepts is declared once on its descriptor, so a channel can offer it, refuse what was never declared
/// and refuse a run missing what the command cannot work without or carrying a value it cannot use.
/// </summary>
/// <param name="Name">The option as the user types it, lowercase and without colon: <c>path</c>, not <c>Path:</c>.</param>
/// <param name="Description">One line telling the user what the value is for.</param>
/// <param name="Required">True when the command cannot run without it, which is refused before anything happens.</param>
/// <param name="DefaultValue">
/// The value the command runs on when the user gives none: a channel proposes it ready to be accepted or
/// edited, while a run carrying nothing for this option is given it rather than refused. A command that
/// knows what a sensible answer looks like on the machine it runs on — a path under the user's own desktop,
/// spelled the way that operating system spells one — says it here instead of leaving the shape to be guessed.
/// It is one value decided once, so a command needing a different one each time asks for it instead.
/// </param>
/// <param name="AllowedValues">
/// The only values the option takes, matched ignoring case; null when any value is acceptable. A value
/// outside them is refused before the command runs, on whichever side of the wire it was typed.
/// </param>
public record CommandOption(
    string Name,
    string Description,
    bool Required = false,
    string? DefaultValue = null,
    IReadOnlyList<string>? AllowedValues = null)
{
    /// <summary>Tells whether <paramref name="value"/> is one this option takes.</summary>
    public bool Allows(string value) =>
        AllowedValues is null || AllowedValues.Any(allowed => string.Equals(allowed, value.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The allowed values as a user reads them, <c>text or json</c>; empty when any value is acceptable.</summary>
    public string DescribeAllowedValues() =>
        AllowedValues is not { Count: > 0 } values
            ? string.Empty
            : values.Count == 1
                ? values[0]
                : $"{string.Join(", ", values.Take(values.Count - 1))} or {values[^1]}";
}