using Morgana.Terminal.Messages;

namespace Rune.Messages;

/// <summary>
/// Rune's look: its emeralds, with a bar written in plain characters as everything else this channel draws is.
/// The look is drawn by the channel itself and is never a capability declared at the handshake, so it leaves the
/// degradation path Rune exists for untouched.
/// </summary>
public static class RuneTerminalTheme
{
    /// <summary>The theme every surface of the channel is drawn in.</summary>
    public static readonly TerminalTheme Instance = new(
        MorganaColor: "#10b981",
        AgentColor: "#6ee7b7",
        ProgressFilledGlyph: '=',
        ProgressEmptyGlyph: '-',
        ProgressWidth: 20);
}
