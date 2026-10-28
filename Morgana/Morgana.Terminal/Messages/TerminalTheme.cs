namespace Morgana.Terminal.Messages;

/// <summary>
/// What a channel decides about the look of its terminal: the colours of Morgana and of the agents plus how a
/// running command's bar is drawn. Every surface the shared library draws wears these, so Grimoire and Rune stay
/// recognisably themselves while sharing the code that draws them.
/// </summary>
/// <param name="MorganaColor">The colour of base Morgana, which also marks the highlighted answer of every command surface, as a Spectre colour.</param>
/// <param name="AgentColor">The colour of a specialised agent, as a Spectre colour.</param>
/// <param name="ProgressFilledGlyph">The cell of the progress bar that is done, in the channel's own character vocabulary.</param>
/// <param name="ProgressEmptyGlyph">The cell still to do, which must draw one column wide like the filled one.</param>
/// <param name="ProgressWidth">How many cells the bar occupies, leaving the row to the step count and the label.</param>
public sealed record TerminalTheme(
    string MorganaColor,
    string AgentColor,
    char ProgressFilledGlyph,
    char ProgressEmptyGlyph,
    int ProgressWidth)
{
    /// <summary>The name the user's own lines are filed under.</summary>
    public const string UserSpeaker = "You";

    /// <summary>The name base Morgana's lines are filed under.</summary>
    public const string MorganaSpeaker = "Morgana";

    /// <summary>The colour of the user's own lines and of the surfaces the user acts on.</summary>
    public const string UserColor = "white";

    /// <summary>The gauge colour once the remaining fraction is at most 30%, as Cauldron's low dust meter.</summary>
    public const string DustLowColor = "#f59e0b";

    /// <summary>The gauge colour once the remaining fraction is at most 10%, as Cauldron's critical dust meter.</summary>
    public const string DustCriticalColor = "#ef4444";

    /// <summary>The colour of advisory warnings such as a rate limit or a low budget.</summary>
    public const string WarningColor = "orange1";

    /// <summary>The colour of error notices such as an exhausted budget or a failed delivery.</summary>
    public const string ErrorColor = "red";

    /// <summary>
    /// The colour of what a command reports: neutral grey, so it reads apart from the speakers (a command is not
    /// a turn of the conversation) and apart from a warning, which is about the conversation.
    /// </summary>
    public const string CommandReportColor = "grey70";

    /// <summary>The colour a speaker's rows are drawn in: white for the user, <see cref="MorganaColor"/> for Morgana and <see cref="AgentColor"/> for any specialised agent.</summary>
    public string SpeakerColor(string speaker)
    {
        // The user's own lines
        if (speaker.Equals(UserSpeaker, StringComparison.OrdinalIgnoreCase))
            return UserColor;

        // Base Morgana
        if (speaker.Equals(MorganaSpeaker, StringComparison.OrdinalIgnoreCase))
            return MorganaColor;

        // Anyone else is a specialised agent
        return AgentColor;
    }
}
