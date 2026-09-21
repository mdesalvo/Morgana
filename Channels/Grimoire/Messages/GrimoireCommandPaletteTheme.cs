using Morgana.Terminal.Messages;

namespace Grimoire.Messages;

/// <summary>Grimoire's primary on the shared command palette: Morgana's purple, Cauldron's <c>#8b5cf6</c>.</summary>
public static class GrimoireCommandPaletteTheme
{
    /// <summary>The theme the palette is drawn in.</summary>
    public static readonly CommandPaletteTheme Instance = new(PrimaryColor: "#8b5cf6");
}
