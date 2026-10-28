using Morgana.Terminal.Messages;

namespace Grimoire.Messages;

/// <summary>Grimoire's look: Cauldron's purple <c>#8b5cf6</c> for Morgana and its pink <c>#ec4899</c> for the agents, with a bar of solid blocks as a rich terminal draws one.</summary>
public static class GrimoireTerminalTheme
{
    /// <summary>The theme every surface of the channel is drawn in.</summary>
    public static readonly TerminalTheme Instance = new(
        MorganaColor: "#8b5cf6",
        AgentColor: "#ec4899",
        ProgressFilledGlyph: '█',
        ProgressEmptyGlyph: '░',
        ProgressWidth: 24);
}
