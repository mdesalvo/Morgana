using Morgana.Contracts;
using Morgana.Terminal.Messages;
using Spectre.Console;

namespace Morgana.Terminal.Services;

/// <summary>
/// Shows the progress that a running command reports, as a TTY channel draws it in its own <see cref="TerminalTheme"/>.
/// Frames replace one another and none reaches the transcript, so a command that took twenty steps leaves the conversation as
/// it found it. A rich terminal's bar of solid blocks and a poor one's row of plain characters are the same widget in each
/// channel's own vocabulary. It holds the current frame, so every call comes under the render lock of the UI that owns it.
/// Drawn here rather than taken from Spectre, whose ProgressBar is internal while its public progress API takes the terminal
/// over, which the live display of these channels already owns.
/// </summary>
public sealed class CommandProgressService
{
    /// <summary>The style of what is left to do, dimmed so that the filled part reads as the measure.</summary>
    private const string RemainingStyle = "grey42";

    /// <summary>The style of the command's name and the step count beside the bar.</summary>
    private const string CaptionStyle = "grey70";

    /// <summary>Measures and cuts the row to the width that the UI's row budget counts on.</summary>
    private readonly TerminalCellService cells;

    /// <summary>The channel's colour and character vocabulary, which the whole widget is drawn in.</summary>
    private readonly TerminalTheme theme;

    /// <summary>Builds the widget on the cell measurement and the channel's theme.</summary>
    public CommandProgressService(TerminalCellService cells, TerminalTheme theme)
    {
        // The widget measures its row with the UI's own cells and paints it in the channel's theme.
        this.cells = cells;
        this.theme = theme;
    }

    /// <summary>The frame on screen; null when no command is reporting.</summary>
    private CommandProgress? currentFrame;

    /// <summary>Tells whether a command is reporting progress, which is what puts the widget in place of the prompt.</summary>
    public bool IsActive => currentFrame is not null;

    /// <summary>Takes <paramref name="frame"/> as the state of the command reporting it, a frame marked finished taking the widget off the screen.</summary>
    public void Show(CommandProgress frame) => currentFrame = frame.Finished ? null : frame;

    /// <summary>Takes the widget off the screen, for a command that stopped reporting without ever finishing.</summary>
    public void Clear() => currentFrame = null;

    /// <summary>
    /// The widget as one row of at most <paramref name="width"/> cells: the bar, the step count and what is being worked on; empty when no command is reporting.
    /// One row, so the UI budgets it as it budgets its other prompt rows.
    /// </summary>
    public List<Markup> RenderProgress(int width)
    {
        // No command is reporting, so there is nothing to draw.
        if (currentFrame is not { } frame)
            return [];

        // A terminal reporting no width still gets one cell per row.
        width = Math.Max(1, width);

        // A frame counting no steps at all would draw an empty bar promising nothing, so the widget stays away.
        if (frame.Total <= 0)
            return [];

        // A frame counting past its own total would fill more of the bar than the bar has.
        int completedSteps = Math.Clamp(frame.Completed, 0, frame.Total);
        int filledCells = (int)Math.Round((double)completedSteps / frame.Total * theme.ProgressWidth);

        // The row is built in the three pieces it is painted in, never searched for its own glyphs: a label naming a
        // file with a dash in it would otherwise be read as part of the bar it sits beside. The label arrives from the network.
        string head = $"  {frame.Command} ";
        string filledBar = new(theme.ProgressFilledGlyph, filledCells);
        string remainingBar = new(theme.ProgressEmptyGlyph, theme.ProgressWidth - filledCells);
        string tail = cells.SanitizeForTerminal($" {completedSteps}/{frame.Total}  {frame.Label}");

        // Each piece takes what the ones before it left of the row, so a narrow terminal loses the label first,
        // then the bar, never the name of the command doing the work.
        string drawnHead = cells.Trunc(head, width);
        string drawnFilled = cells.Trunc(filledBar, width - drawnHead.GetCellWidth());
        string drawnRemaining = cells.Trunc(remainingBar, width - drawnHead.GetCellWidth() - drawnFilled.GetCellWidth());
        string drawnTail = cells.Trunc(tail, width - drawnHead.GetCellWidth() - drawnFilled.GetCellWidth() - drawnRemaining.GetCellWidth());

        // The bar is one row: the command, the filled and remaining cells, then the count and the label.
        return
        [
            new Markup($"[{CaptionStyle}]{Markup.Escape(drawnHead)}[/]"
                       + $"[{theme.MorganaColor}]{drawnFilled}[/]"
                       + $"[{RemainingStyle}]{drawnRemaining}[/]"
                       + $"[{CaptionStyle}]{Markup.Escape(drawnTail)}[/]")
        ];
    }
}