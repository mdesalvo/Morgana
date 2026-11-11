namespace Morgana.Contracts;

/// <summary>A value a command is given when it is run, written at the prompt as <c>name:value</c>.</summary>
/// <param name="Name">The option as the user types it, lowercase and without colon: <c>path</c>, not <c>Path:</c>.</param>
/// <param name="Description">One line telling the user what the value is for.</param>
/// <param name="Required">True when the command cannot run without it.</param>
/// <param name="DefaultValue">The value the command runs on when the user gives none.</param>
/// <param name="AllowedValues">The only values the option takes, matched ignoring case; null when any value is acceptable.</param>
// What a command accepts is declared once on its descriptor, so a channel can offer it, refuse what was never declared
// and refuse a run missing what the command cannot work without or carrying a value it cannot use. A missing required option
// is refused before anything happens and so is a value outside AllowedValues, on whichever side of the wire it was typed.
// A channel proposes DefaultValue ready to be accepted or edited and a run carrying nothing for the option is given it rather than refused.
// It is one value decided once: a command needing a different one each time asks for it instead. A command that knows the shape of
// a sensible answer on its machine, such as a path under the user's desktop spelled the way that operating system spells one, states it here.
public record CommandOption(
    string Name,
    string Description,
    bool Required = false,
    string? DefaultValue = null,
    IReadOnlyList<string>? AllowedValues = null)
{
    /// <summary>Tells whether <paramref name="value"/> is one this option takes.</summary>
    public bool Allows(string value) =>
        // Without a declared list every value passes; with one the trimmed value must match an entry ignoring case.
        AllowedValues is null || AllowedValues.Any(allowed => string.Equals(allowed, value.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The allowed values as a user reads them, <c>text or json</c>; empty when any value is acceptable.</summary>
    public string DescribeAllowedValues() =>
        // No list reads as nothing to say; a lone value stands alone; several read as a sentence ending in "or".
        AllowedValues is not { Count: > 0 }
            ? string.Empty
            : AllowedValues.Count == 1
                ? AllowedValues[0]
                : $"{string.Join(", ", AllowedValues.Take(AllowedValues.Count - 1))} or {AllowedValues[^1]}";
}
