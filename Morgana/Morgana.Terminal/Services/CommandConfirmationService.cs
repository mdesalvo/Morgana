using Morgana.Contracts;
using Morgana.Terminal.Messages;
using Spectre.Console;

namespace Morgana.Terminal.Services;

/// <summary>
/// The Yes/No question that stands between a command declaring <see cref="CommandDescriptor.RequiresConfirmation"/>
/// and its running: which command is waiting on an answer, which option is highlighted, what the keys mean and how
/// the question is drawn. Every TTY channel asks it the same way, in its own
/// <see cref="Messages.CommandPaletteTheme"/> — a channel with no quick replies included, since this is local chrome
/// and not a capability. It holds the pending question, so every call must come under the render lock of the UI.
/// </summary>
public sealed class CommandConfirmationService
{
    /// <summary>Marks the question as the one thing the prompt is waiting on, in the same amber both channels warn in.</summary>
    private const string QuestionStyle = "orange1";

    /// <summary>The option that is not highlighted, leaving the channel's primary colour to the one that is.</summary>
    private const string OptionColor = "white";

    /// <summary>Controls line: a terminal gives no cue that an answer is expected, so the keys are spelled out.</summary>
    private const string ControlsHint = "y confirm · n cancel · ←→ move · Enter answer · Esc cancel";

    /// <summary>Style of the controls line, the palette's own de-emphasised grey.</summary>
    private const string HintStyle = "grey54 italic";

    /// <summary>Measures and cuts every row to the width the UI's row budget counts on.</summary>
    private readonly TerminalCellService cells;

    /// <summary>The channel's primary colour, which marks the highlighted answer.</summary>
    private readonly CommandPaletteTheme theme;

    /// <summary>Whether the highlight sits on Yes; No holds it until the user moves it, so a stray Enter runs nothing.</summary>
    private bool yesHighlighted;

    /// <summary>Captures the cell measurement and the channel's theme.</summary>
    public CommandConfirmationService(TerminalCellService cells, CommandPaletteTheme theme)
    {
        this.cells = cells;
        this.theme = theme;
    }

    /// <summary>The command waiting on an answer, with the values it would run on; null when the prompt is free.</summary>
    public CommandInvocation? PendingInvocation { get; private set; }

    /// <summary>Tells whether a question is on screen, which suspends every other use of the keyboard while it lasts.</summary>
    public bool IsPending => PendingInvocation is not null;

    /// <summary>Puts <paramref name="invocation"/>'s question on screen, with the highlight on No.</summary>
    public void Ask(CommandInvocation invocation)
    {
        PendingInvocation = invocation;
        yesHighlighted = false;
    }

    /// <summary>
    /// Answers the question with <paramref name="key"/>: y and n decide outright, the arrows move the highlight,
    /// Enter takes what is highlighted and Esc walks away. Every other key leaves the question standing.
    /// Reading <see cref="PendingInvocation"/> before the call is the only way to learn which command a
    /// <see cref="ConfirmationOutcome.Confirmed"/> belongs to, since the question is closed by either answer.
    /// </summary>
    public ConfirmationOutcome HandleKey(ConsoleKeyInfo key)
    {
        if (!IsPending)
            return ConfirmationOutcome.Pending;

        switch (key.Key)
        {
            // Both axes are accepted so the user needs not guess how the two options are laid out
            case ConsoleKey.LeftArrow or ConsoleKey.RightArrow or ConsoleKey.UpArrow or ConsoleKey.DownArrow or ConsoleKey.Tab:
                yesHighlighted = !yesHighlighted;
                return ConfirmationOutcome.Pending;

            case ConsoleKey.Enter:
                return Close(yesHighlighted);

            // Esc is what leaves every other terminal state; here it can only mean the command was a mistake
            case ConsoleKey.Escape:
                return Close(false);
        }

        // The initials answer without a trip through the highlight, which is how a question like this is usually
        // answered; anything else typed is swallowed, since prose has no meaning against a Yes/No
        return char.ToLowerInvariant(key.KeyChar) switch
        {
            'y' => Close(true),
            'n' => Close(false),
            _ => ConfirmationOutcome.Pending
        };
    }

    /// <summary>
    /// Draws the question: what is about to run, the two answers and the controls line. Every row is one markup
    /// of at most <paramref name="width"/> cells, so the UI can budget them as it budgets the palette's.
    /// Empty when no command is waiting.
    /// </summary>
    public List<Markup> RenderQuestion(int width)
    {
        if (PendingInvocation is not { } invocation)
            return [];

        // A terminal reporting no width still gets one cell per row
        width = Math.Max(1, width);

        // The values are named in the question because they are usually what makes the gesture irreversible:
        // which file is about to be written over is the thing to read before answering
        string values = invocation.Options.Count == 0
            ? string.Empty
            : " " + string.Join(' ', invocation.Options.Select(option => $"{option.Key}:{option.Value}"));

        // The name alone would not say what is about to happen to the conversation, so the command's own line comes with it
        string question = SanitizeForTerminal($"⚠ Run /{invocation.Command.Name}{values}? {invocation.Command.Description}");

        // The highlighted answer wears the channel's primary inverted, the shape the palette's caret row already has
        string yes = RenderAnswer("Yes", yesHighlighted);
        string no = RenderAnswer("No", !yesHighlighted);

        return
        [
            new Markup($"[{QuestionStyle}]{Markup.Escape(cells.Trunc(question, width))}[/]"),
            new Markup($"  {yes}  {no}"),
            new Markup($"[{HintStyle}]{Markup.Escape(cells.Trunc(ControlsHint, width))}[/]")
        ];
    }

    /// <summary>Draws one answer, padded so both read as buttons whether or not the highlight is on them.</summary>
    private string RenderAnswer(string label, bool highlighted) =>
        highlighted
            ? $"[{theme.PrimaryColor} invert] {label} [/]"
            : $"[{OptionColor}] {label} [/]";

    /// <summary>Closes the question, answering it with <paramref name="confirmed"/>.</summary>
    private ConfirmationOutcome Close(bool confirmed)
    {
        PendingInvocation = null;
        return confirmed ? ConfirmationOutcome.Confirmed : ConfirmationOutcome.Declined;
    }

    /// <summary>
    /// Prepares text published by Morgana for the terminal. A command's description arrives from the network, so
    /// control characters are stripped before they can steer the user's terminal. Emoji are resolved and variation
    /// selectors dropped first, so the width measured is the width drawn.
    /// </summary>
    private string SanitizeForTerminal(string text) =>
        TerminalCellService.StripControlCharacters(cells.StripVariationSelectors(Emoji.Replace(text)));
}