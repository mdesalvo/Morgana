namespace Distiller.Model;

/// <summary>
/// What a pass wrote for one turn, told apart into the two things the client reads: what they are
/// being told and what they are being asked.
/// </summary>
/// <remarks>
/// One place, because the screen and the harness have to agree on where the line falls: the screen
/// draws the two in two different voices and the harness holds every step to opening with one.
/// </remarks>
public static class AskedTurn
{
    /// <summary>
    /// Splits a turn into the sentence placing it and the question itself.
    /// </summary>
    /// <param name="asked">The whole of what the pass wrote this turn.</param>
    /// <returns>The placing sentence, or <c>null</c> where the turn only asks, and the question.</returns>
    /// <remarks>
    /// The blank line is the shape the doctrine asks for and it is honoured first. Where a pass ran
    /// the two together anyway, the question is still the last sentence and the only one carrying a
    /// question mark, so it is found by reading rather than by trusting the layout: a step that
    /// placed itself perfectly well in prose must not lose that placing on the screen because of a
    /// newline nobody can see.
    /// </remarks>
    public static (string? Placing, string Asking) Parted(string asked)
    {
        string whole = asked.Replace("\r\n", "\n").Trim();
        int blank = whole.LastIndexOf("\n\n", StringComparison.Ordinal);

        if (blank >= 0)
            return (whole[..blank].Trim(), whole[(blank + 2)..].Trim());

        int question = whole.LastIndexOf('?');

        if (question < 0)
            return (null, whole);

        // Where the question itself starts: after whatever sentence closed before it. A turn that is
        // only a question has nothing closing before it and nothing to place it with.
        int opens = whole[..question].LastIndexOfAny(['.', '!', '?', '\n']);

        return opens < 0 ? (null, whole) : (whole[..(opens + 1)].Trim(), whole[(opens + 1)..].Trim());
    }
}
