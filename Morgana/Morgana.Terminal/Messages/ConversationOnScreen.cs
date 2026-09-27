using Morgana.Contracts;
using Morgana.Terminal.Services;

namespace Morgana.Terminal.Messages;

/// <summary>
/// A conversation about to take the screen. A fresh one still has its presentation on the way. One resumed
/// from Morgana's record arrives with everything already said and nothing more to wait for.
/// </summary>
/// <param name="ConversationId">The conversation the header names from now on.</param>
/// <param name="Resumed">What Morgana reported on resuming it; null for a fresh conversation.</param>
/// <param name="Transcript">The conversation as Morgana has it on record, in chronological order; empty for a fresh one.</param>
public sealed record ConversationOnScreen(
    string ConversationId,
    ResumeConversationResponse? Resumed,
    IReadOnlyList<MorganaChatMessage> Transcript)
{
    /// <summary>A conversation just opened, whose first delivery the prompt waits for.</summary>
    public static ConversationOnScreen Fresh(string conversationId) => new(conversationId, null, []);

    /// <summary>
    /// Who the header names on arrival: Morgana herself unless the record shows an agent carrying the
    /// conversation, which is then spelled as its deliveries spell it. Cleaned like every name a
    /// terminal prints, so nothing in it is obeyed instead of shown.
    /// </summary>
    public string Speaker =>
        Resumed?.ActiveAgent is { Length: > 0 } agent && !string.Equals(agent, "Morgana", StringComparison.OrdinalIgnoreCase)
            ? TerminalCellService.StripControlCharacters($"Morgana ({char.ToUpperInvariant(agent[0])}{agent[1..]})")
            : "Morgana";
}
