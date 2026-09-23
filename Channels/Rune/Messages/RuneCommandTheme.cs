using Morgana.Terminal.Messages;

namespace Rune.Messages;

/// <summary>
/// Rune's look on the command surfaces: its emerald, with a bar written in plain characters, as everything
/// else this channel draws is. The command surfaces are drawn by the channel itself, never a capability
/// declared at the handshake, so they leave the degradation path Rune exists for untouched.
/// </summary>
public static class RuneCommandTheme
{
    /// <summary>The theme every command surface is drawn in.</summary>
    public static readonly CommandTheme Instance = new(
        PrimaryColor: "#10b981",
        ProgressFilledGlyph: '=',
        ProgressEmptyGlyph: '-',
        ProgressWidth: 20);
}
