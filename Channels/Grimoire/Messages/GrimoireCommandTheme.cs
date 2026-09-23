using Morgana.Terminal.Messages;

namespace Grimoire.Messages;

/// <summary>Grimoire's look on the command surfaces: Morgana's purple, Cauldron's <c>#8b5cf6</c>, with a bar of solid blocks as a rich terminal draws one.</summary>
public static class GrimoireCommandTheme
{
    /// <summary>The theme every command surface is drawn in.</summary>
    public static readonly CommandTheme Instance = new(
        PrimaryColor: "#8b5cf6",
        ProgressFilledGlyph: '\u2588',
        ProgressEmptyGlyph: '\u2591',
        ProgressWidth: 24);
}
