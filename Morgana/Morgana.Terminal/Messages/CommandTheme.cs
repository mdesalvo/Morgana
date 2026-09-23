namespace Morgana.Terminal.Messages;

/// <summary>
/// What a channel decides about the look of the command system: the palette's highlight, the answer
/// highlighted on a confirmation question and how a running command's bar is drawn. Everything a command
/// surface shows wears these, so Grimoire and Rune stay recognisably themselves while sharing the code
/// that draws them.
/// </summary>
/// <param name="PrimaryColor">The channel's primary brand colour, as a Spectre colour.</param>
/// <param name="ProgressFilledGlyph">The cell of the progress bar that is done, in the channel's own character vocabulary.</param>
/// <param name="ProgressEmptyGlyph">The cell still to do, which must draw one column wide like the filled one.</param>
/// <param name="ProgressWidth">How many cells the bar occupies, leaving the row to the step count and the label.</param>
public sealed record CommandTheme(
    string PrimaryColor,
    char ProgressFilledGlyph,
    char ProgressEmptyGlyph,
    int ProgressWidth);
