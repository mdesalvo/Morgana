using Morgana.Contracts;
using Morgana.Terminal.Messages;
using Spectre.Console;

namespace Morgana.Terminal.Services;

/// <summary>The Yes/No question that stands between a command declaring <see cref="CommandDescriptor.RequiresConfirmation"/> and its running.</summary>
// Every TTY channel asks it the same way in its own TerminalTheme, a channel with no quick replies included: the channel draws
// the question itself, so it is never a capability it had to declare. It holds the pending question, so every call comes under
// the render lock of the UI.
public sealed class CommandConfirmationService
{
    /// <summary>Marks the question as the one thing the prompt is waiting on, in the amber both channels warn in.</summary>
    private const string QuestionStyle = "orange1";

    /// <summary>The colour of the answer that is not highlighted, leaving the channel's Morgana colour to the one that is.</summary>
    private const string OptionColor = "white";

    /// <summary>The controls line, since a terminal gives no cue that an answer is expected.</summary>
    private const string ControlsHint = "y confirm · n cancel · ←→ move · Enter answer · Esc cancel";

    /// <summary>The style of the controls line, the palette's own dimmed grey.</summary>
    private const string HintStyle = "grey54 italic";

    /// <summary>Measures and cuts every row to the width that the UI's row budget counts on.</summary>
    private readonly TerminalCellService cells;

    /// <summary>The channel's Morgana colour, which marks the highlighted answer.</summary>
    private readonly TerminalTheme theme;

    /// <summary>Whether the highlight sits on Yes.</summary>
    // No holds it until the user moves it, so a stray Enter runs nothing.
    private bool yesHighlighted;

    /// <summary>Builds the question on the cell measurement and the channel's theme.</summary>
    public CommandConfirmationService(TerminalCellService cells, TerminalTheme theme)
    {
        // The question measures its rows with the UI's own cells and paints them in the channel's theme.
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
        // The question belongs to this run and starts on the answer that runs nothing.
        PendingInvocation = invocation;
        yesHighlighted = false;
    }

    /// <summary>Answers the question with <paramref name="key"/>: y and n decide, the arrows move the highlight, Enter takes it and Esc walks away.</summary>
    // Every other key leaves the question standing. Either answer closes the question, so reading PendingInvocation before the
    // call is the only way to learn which command a Confirmed belongs to.
    public ConfirmationOutcome HandleKey(ConsoleKeyInfo key)
    {
        // With no question on screen there is nothing to answer.
        if (!IsPending)
            return ConfirmationOutcome.Pending;

        // Keys not listed here fall to the initials below.
        switch (key.Key)
        {
            // Both axes are accepted, so the user needs not guess how the two answers are laid out.
            case ConsoleKey.LeftArrow or ConsoleKey.RightArrow or ConsoleKey.UpArrow or ConsoleKey.DownArrow or ConsoleKey.Tab:
                yesHighlighted = !yesHighlighted;
                return ConfirmationOutcome.Pending;

            // Enter takes the answer that is highlighted.
            case ConsoleKey.Enter:
                return Close(yesHighlighted);

            // Esc is what leaves every other terminal state; here it can only mean the command was a mistake.
            case ConsoleKey.Escape:
                return Close(false);
        }

        // The initials answer without a trip through the highlight, which is how a question like this is usually answered;
        // anything else typed is swallowed, since prose has no meaning against a Yes/No.
        return char.ToLowerInvariant(key.KeyChar) switch
        {
            // Y is a Yes outright.
            'y' => Close(true),
            // N is a No outright.
            'n' => Close(false),
            // Anything else leaves the question standing.
            _ => ConfirmationOutcome.Pending
        };
    }

    /// <summary>The question as rows of at most <paramref name="width"/> cells: what is about to run, the two answers and the controls; empty when no command waits.</summary>
    // Every row is one markup, so the UI budgets them as it budgets the palette's.
    public List<Markup> RenderQuestion(int width)
    {
        // No command is waiting, so there is nothing to draw.
        if (PendingInvocation is not { } invocation)
            return [];

        // A terminal reporting no width still gets one cell per row.
        width = Math.Max(1, width);

        // The values are named in the question because they are usually what makes the gesture irreversible: which file is
        // about to be written over is the thing to read before answering.
        string values = invocation.Options.Count == 0
            ? string.Empty
            : " " + string.Join(' ', invocation.Options.Select(option => $"{option.Key}:{option.Value}"));

        // The name alone would not say what is about to happen to the conversation, so the command's own line comes with it;
        // that line arrives from the network.
        string question = cells.SanitizeForTerminal($"⚠ Run /{invocation.Command.Name}{values}? {invocation.Command.Description}");

        // The highlighted answer wears the channel's Morgana colour inverted, the shape the palette's caret row already has.
        string yes = RenderAnswer("Yes", yesHighlighted);
        string no = RenderAnswer("No", !yesHighlighted);

        // The question, the two answers and the controls line, one row each.
        return
        [
            new Markup($"[{QuestionStyle}]{Markup.Escape(cells.Trunc(question, width))}[/]"),
            new Markup($"  {yes}  {no}"),
            new Markup($"[{HintStyle}]{Markup.Escape(cells.Trunc(ControlsHint, width))}[/]")
        ];
    }

    /// <summary>Draws one answer, padded so that both read as buttons whether or not the highlight is on them.</summary>
    private string RenderAnswer(string label, bool isHighlighted) =>
        isHighlighted
            ? $"[{theme.MorganaColor} invert] {label} [/]"
            : $"[{OptionColor}] {label} [/]";

    /// <summary>Closes the question, answering it with <paramref name="confirmed"/>.</summary>
    private ConfirmationOutcome Close(bool confirmed)
    {
        // The question is gone from the prompt whatever the answer.
        PendingInvocation = null;
        return confirmed ? ConfirmationOutcome.Confirmed : ConfirmationOutcome.Declined;
    }
}