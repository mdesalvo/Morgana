using System.Text;
using Grimoire.Messages.Contracts;
using Spectre.Console;

namespace Grimoire.Services;

/// <summary>
/// Renders a turn's quick replies into single-row Spectre <see cref="Markup"/>s — the
/// terminal-side counterpart of Cauldron's <c>QuickReplyButton</c> component. In Grimoire the
/// quick replies <em>are</em> the prompt for the turn that offers them (<see cref="ConsoleUiService"/>
/// suspends the text input and routes the arrow keys here), so this renders the selectable surface
/// that replaces the bottom input line — not an inline strip under a chat bubble.
/// </summary>
/// <remarks>
/// <para>
/// Form adapts to option count: <b>≤2 options render horizontally</b> side-by-side (the "comfortable"
/// case), <b>more than two stack vertically</b>. A horizontal row that would overrun the terminal
/// width falls back to vertical, so the layout never breaks the "one <see cref="Markup"/> = one
/// terminal row" invariant <see cref="ConsoleUiService.BuildBody"/> budgets against.
/// </para>
/// <para>
/// Parity with Cauldron: the <see cref="QuickReply.Label"/> is shown, the highlighted option reads
/// inverted (dark text on the speaker/accent colour), and a <see cref="QuickReply.Termination"/>
/// reply is tinted red to signal it closes the branch — exactly the role Cauldron's
/// <c>.quick-reply-btn.termination</c> CSS class plays. The actual send (<see cref="QuickReply.Value"/>)
/// is the caller's job; this service is pure presentation.
/// </para>
/// </remarks>
public static class QuickReplyTerminalRenderService
{
    /// <summary>Foreground for the non-selected options — the same de-emphasised grey the rich-card body uses.</summary>
    private const string InactiveForeground = "grey70";

    /// <summary>Style of the leading affordance hint line.</summary>
    private const string HintStyle = "grey54 italic";

    /// <summary>Highlight style for the selected option when it terminates the branch (red, since it ends the conversation thread).</summary>
    private const string TerminationActive = "white on red";

    /// <summary>Resting style for a non-selected termination option.</summary>
    private const string TerminationInactive = "red";

    /// <summary>Affordance line: a TTY has no obvious "clickable button" cue, so spell out the controls.</summary>
    private const string HintText = "↑↓ move · Enter choose · Esc quit";

    /// <summary>
    /// Renders <paramref name="options"/> with the entry at <paramref name="selectedIndex"/>
    /// highlighted, tinting the active option / carets with <paramref name="accentColor"/>
    /// (the channel's user colour). The first row is always the controls hint; the option
    /// rows follow in the adaptive horizontal/vertical form. <paramref name="width"/> is the
    /// terminal width available.
    /// </summary>
    public static List<Markup> RenderRows(IReadOnlyList<QuickReply> options, int selectedIndex, string accentColor, int width)
    {
        int termWidth = Math.Max(1, width);
        List<Markup> rows = WrapHint(termWidth);
        if (options.Count == 0)
            return rows; // defensive: callers only enter QR mode with a non-empty set

        // ≤2 reads best on one line — but only if the chips actually fit; otherwise drop to the
        // vertical list, which always fits because each option is truncated to the row width.
        if (options.Count <= 2 && TryRenderHorizontal(options, selectedIndex, accentColor, termWidth, out Markup horizontal))
        {
            rows.Add(horizontal);
            return rows;
        }

        for (int i = 0; i < options.Count; i++)
            rows.Add(RenderVerticalOption(options[i], i == selectedIndex, accentColor, termWidth));
        return rows;
    }

    /// <summary>
    /// Builds all options as chips on a single row separated by two spaces, measuring visible
    /// columns as it goes (style tags cost none). Returns false — and the caller goes vertical —
    /// when the assembled row would exceed <paramref name="width"/>.
    /// </summary>
    private static bool TryRenderHorizontal(IReadOnlyList<QuickReply> options, int selectedIndex, string accentColor, int width, out Markup row)
    {
        StringBuilder sb = new(width + 64);
        int visible = 0;
        for (int i = 0; i < options.Count; i++)
        {
            if (i > 0)
            {
                sb.Append("  ");
                visible += 2;
            }
            bool active = i == selectedIndex;
            AppendCaret(sb, active, accentColor);
            string label = $" {options[i].Label} "; // one space of chip padding each side
            sb.Append('[').Append(ChipStyle(options[i], active, accentColor)).Append(']').Append(Markup.Escape(label)).Append("[/]");
            visible += 2 /* caret */ + label.Length;
        }
        row = new Markup(sb.ToString());
        return visible <= width;
    }

    /// <summary>Builds one option as its own row: a caret, then the space-padded label. The label is truncated so caret(2) + padding(2) + label never exceeds <paramref name="width"/>.</summary>
    private static Markup RenderVerticalOption(QuickReply reply, bool active, string accentColor, int width)
    {
        string label = Trunc(reply.Label, Math.Max(1, width - 4));
        StringBuilder sb = new(width + 32);
        AppendCaret(sb, active, accentColor);
        sb.Append('[').Append(ChipStyle(reply, active, accentColor)).Append("] ").Append(Markup.Escape(label)).Append(" [/]");
        return new Markup(sb.ToString());
    }

    /// <summary>Appends the two-column caret: a tinted <c>❯</c> on the active option, two blanks otherwise. Visible width is 2 either way, keeping options aligned.</summary>
    private static void AppendCaret(StringBuilder sb, bool active, string accentColor)
    {
        if (active)
            sb.Append('[').Append(accentColor).Append("]❯[/] ");
        else
            sb.Append("  ");
    }

    /// <summary>
    /// The chip body style. A normal option inverts to dark-on-accent when selected and rests on
    /// the dim grey otherwise; a termination option swaps that ramp for red so "this ends the
    /// thread" reads at a glance.
    /// </summary>
    private static string ChipStyle(QuickReply reply, bool active, string accentColor)
    {
        if (reply.Termination)
            return active ? TerminationActive : TerminationInactive;
        return active ? $"black on {accentColor}" : InactiveForeground;
    }

    /// <summary>Renders the controls hint, char-wrapped to <paramref name="width"/> so it too honours one-Markup-per-row on a narrow terminal.</summary>
    private static List<Markup> WrapHint(int width)
    {
        List<Markup> rows = [];
        for (int offset = 0; offset < HintText.Length; offset += width)
            rows.Add(new Markup($"[{HintStyle}]{Markup.Escape(HintText.Substring(offset, Math.Min(width, HintText.Length - offset)))}[/]"));
        return rows;
    }

    /// <summary>Truncates to <paramref name="width"/> columns, appending an ellipsis when it has to cut (and there's room for one).</summary>
    private static string Trunc(string text, int width)
    {
        width = Math.Max(1, width);
        if (text.Length <= width)
            return text;
        return width >= 2 ? text[..(width - 1)] + "…" : text[..width];
    }
}
