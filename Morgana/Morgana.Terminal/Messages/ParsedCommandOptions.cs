namespace Morgana.Terminal.Messages;

/// <summary>
/// The options read off a command line, with what kept the line from being read when it could not be.
/// Nothing is run on a line that could not be read, so a caller seeing a Problem runs nothing.
/// </summary>
/// <param name="Options">The options keyed by name ignoring case, never null; partial when <paramref name="Problem"/> is set.</param>
/// <param name="Problem">The line to show the user when the options could not be read; null when they were.</param>
public sealed record ParsedCommandOptions(IReadOnlyDictionary<string, string> Options, string? Problem);