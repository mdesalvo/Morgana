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
/// Options always stack <b>vertically</b>, one per row, regardless of count — chosen for usability
/// and predictability over a compact horizontal strip. Each option is truncated to the row width,
/// so the layout never breaks the "one <see cref="Markup"/> = one terminal row" invariant
/// <see cref="ConsoleUiService.BuildBody"/> budgets against.
/// </para>
/// <para>
/// Parity with Cauldron: the <see cref="QuickReply.Label"/> is shown and the highlighted option
/// reads inverted (dark text on the speaker/accent colour). A <see cref="QuickReply.Termination"/>
/// reply is tinted with the Morgana primary purple instead — where Cauldron's
/// <c>.quick-reply-btn.termination</c> class warns in red, Grimoire reads the close more kindly:
/// the primary signals that picking it ends the specialised branch and hands the conversation
/// back to base Morgana. The actual send (<see cref="QuickReply.Value"/>) is the caller's job;
/// this service is pure presentation.
/// </para>
/// </remarks>
public static class QuickReplyTerminalRenderService
{
    /// <summary>Foreground for the non-selected options — the same de-emphasised grey the rich-card body uses.</summary>
    private const string InactiveForeground = "grey70";

    /// <summary>Style of the leading affordance hint line.</summary>
    private const string HintStyle = "grey54 italic";

    /// <summary>Highlight style for the selected option when it terminates the branch: Morgana primary (<c>#8b5cf6</c>, matching <c>ConsoleUiService.MorganaColor</c> / Cauldron's <c>--primary-color</c>), signalling the return to base Morgana once the branch closes.</summary>
    private const string TerminationActive = "white on #8b5cf6";

    /// <summary>Resting style for a non-selected termination option — Morgana primary as foreground.</summary>
    private const string TerminationInactive = "#8b5cf6";

    /// <summary>Affordance line: a TTY has no obvious "clickable button" cue, so spell out the controls.</summary>
    private const string HintText = "↑↓ move · Enter choose · Esc quit";

    /// <summary>
    /// Renders <paramref name="quickReplies"/> with the entry at <paramref name="selectedIndex"/>
    /// highlighted, tinting the active option / caret with <paramref name="accentColor"/>
    /// (the channel's user colour). The first row is always the controls hint; the option rows
    /// follow, one per row. <paramref name="width"/> is the terminal width available.
    /// </summary>
    public static List<Markup> RenderQuickReplies(IReadOnlyList<QuickReply> quickReplies, int selectedIndex, string accentColor, int width)
    {
        int termWidth = Math.Max(1, width);
        List<Markup> rows = WrapHint(termWidth);
        // Always vertical, one option per row, whatever the count (callers only enter QR mode with
        // a non-empty set, so an empty loop here is simply a no-op beyond the hint).
        for (int i = 0; i < quickReplies.Count; i++)
            rows.Add(RenderQuickReply(quickReplies[i], i == selectedIndex, accentColor, termWidth));
        return rows;
    }

    /// <summary>Builds one option as its own row: a caret, then the space-padded label. The label is truncated so caret(2) + padding(2) + label never exceeds <paramref name="width"/>.</summary>
    private static Markup RenderQuickReply(QuickReply quickReply, bool active, string accentColor, int width)
    {
        string label = Trunc(quickReply.Label, Math.Max(1, width - 4));
        StringBuilder sb = new(width + 32);
        AppendCaret(sb, active, accentColor);
        sb.Append('[').Append(ChipStyle(quickReply, active, accentColor)).Append("] ").Append(Markup.Escape(label)).Append(" [/]");
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
    /// the dim grey otherwise; a termination option swaps that ramp for the Morgana primary so
    /// "this closes the branch — back to Morgana" reads at a glance.
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