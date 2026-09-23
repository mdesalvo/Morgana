using Morgana.Contracts;
using Morgana.Terminal.Messages;
using Spectre.Console;

namespace Morgana.Terminal.Services;

/// <summary>
/// The progress a running command reports, as a TTY channel shows it: which frame is current, how the bar
/// is drawn and when the widget leaves the screen. Frames replace one another and none reaches the
/// transcript, so a command that took twenty steps leaves the conversation exactly as it found it. Every
/// TTY channel draws it in its own <see cref="CommandTheme"/>, glyphs included, so a rich terminal's
/// bar of solid blocks and a poor one's row of plain characters are the same widget wearing each channel's
/// own vocabulary. It holds the current frame, so every call must come under the render lock of the UI that
/// owns it.
/// </summary>
/// <remarks>
/// Drawn here rather than taken from Spectre: its own <c>ProgressBar</c> is internal to the library, while the
/// public progress API takes the terminal over, which the live display of these channels already owns.
/// </remarks>
public sealed class CommandProgressService
{
    /// <summary>Style of what is left to do, dimmed so the filled part reads as the measure.</summary>
    private const string RemainingStyle = "grey42";

    /// <summary>Style of the command's name and the step count beside the bar.</summary>
    private const string CaptionStyle = "grey70";

    /// <summary>Measures and cuts the row to the width the UI's row budget counts on.</summary>
    private readonly TerminalCellService cells;

    /// <summary>The channel's colour and character vocabulary, which the whole widget is drawn in.</summary>
    private readonly CommandTheme theme;

    /// <summary>Captures the cell measurement and the channel's theme.</summary>
    public CommandProgressService(TerminalCellService cells, CommandTheme theme)
    {
        this.cells = cells;
        this.theme = theme;
    }

    /// <summary>The frame on screen, null when no command is reporting.</summary>
    private CommandProgress? currentFrame;

    /// <summary>Tells whether a command is reporting progress, which is what puts the widget in place of the prompt.</summary>
    public bool IsActive => currentFrame is not null;

    /// <summary>
    /// Takes <paramref name="frame"/> as the state of the command reporting it, replacing the frame before
    /// it; a frame marked finished takes the widget off the screen.
    /// </summary>
    public void Show(CommandProgress frame) => currentFrame = frame.Finished ? null : frame;

    /// <summary>Takes the widget off the screen, for a command that stopped reporting without ever finishing.</summary>
    public void Clear() => currentFrame = null;

    /// <summary>
    /// Draws the widget: the bar, the step count and what is being worked on. One markup of at most
    /// <paramref name="width"/> cells, so the UI budgets it as it budgets its other prompt rows. Empty
    /// when no command is reporting.
    /// </summary>
    public List<Markup> RenderProgress(int width)
    {
        if (currentFrame is not { } frame)
            return [];

        // A terminal reporting no width still gets one cell per row
        width = Math.Max(1, width);

        // A frame counting no steps at all would draw an empty bar promising nothing, so the widget stays away
        if (frame.Total <= 0)
            return [];

        // A frame counting past its own total would fill more of the bar than the bar has
        int completed = Math.Clamp(frame.Completed, 0, frame.Total);
        int filledCells = (int)Math.Round((double)completed / frame.Total * theme.ProgressWidth);

        // The row is built in the three pieces it is painted in, never searched for its own glyphs: a label
        // naming a file with a dash in it would otherwise be read as part of the bar it sits beside
        string head = $"  {frame.Command} ";
        string filled = new(theme.ProgressFilledGlyph, filledCells);
        string remaining = new(theme.ProgressEmptyGlyph, theme.ProgressWidth - filledCells);
        string tail = SanitizeForTerminal($" {completed}/{frame.Total}  {frame.Label}");

        // Each piece takes what the ones before it left of the row, so a narrow terminal loses the label
        // first, then the bar, never the name of the command doing the work
        string drawnHead = cells.Trunc(head, width);
        string drawnFilled = cells.Trunc(filled, width - drawnHead.GetCellWidth());
        string drawnRemaining = cells.Trunc(remaining, width - drawnHead.GetCellWidth() - drawnFilled.GetCellWidth());
        string drawnTail = cells.Trunc(tail, width - drawnHead.GetCellWidth() - drawnFilled.GetCellWidth() - drawnRemaining.GetCellWidth());

        return
        [
            new Markup($"[{CaptionStyle}]{Markup.Escape(drawnHead)}[/]"
                       + $"[{theme.PrimaryColor}]{drawnFilled}[/]"
                       + $"[{RemainingStyle}]{drawnRemaining}[/]"
                       + $"[{CaptionStyle}]{Markup.Escape(drawnTail)}[/]")
        ];
    }

    /// <summary>
    /// Prepares text published by Morgana for the terminal. A label arrives from the network, so control
    /// characters are stripped before they can steer the user's terminal. Emoji are resolved and variation
    /// selectors dropped first, so the width measured is the width drawn.
    /// </summary>
    private string SanitizeForTerminal(string text) =>
        TerminalCellService.StripControlCharacters(cells.StripVariationSelectors(Emoji.Replace(text)));
}