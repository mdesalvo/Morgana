namespace Morgana.Terminal.Messages;

/// <summary>
/// What a channel decides about the shared command palette: its primary colour, which marks the highlighted
/// candidate and a typed line naming a command exactly. Everything else is the same on every TTY channel.
/// </summary>
/// <param name="PrimaryColor">The channel's primary brand colour, as a Spectre colour.</param>
public sealed record CommandPaletteTheme(string PrimaryColor);