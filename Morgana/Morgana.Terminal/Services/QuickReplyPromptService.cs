using System.Text;
using Morgana.Contracts;
using Morgana.Terminal.Messages;
using Spectre.Console;

namespace Morgana.Terminal.Services;

/// <summary>
/// The options a reply offers, as a channel that declares quick replies shows them: which are on offer, which is
/// highlighted, what the keys mean and how they are drawn. While they are on offer they are the prompt of the
/// turn and the free-text line lives among them as one more entry, the compose entry, which the user writes in.
/// The offer is read by the key loop without a lock through <see cref="IsOffering"/>, every other call must come
/// under the render lock of the UI that owns it.
/// </summary>
public sealed class QuickReplyPromptService
{
    /// <summary>Foreground for the options that are not highlighted, the de-emphasised grey the rich card body uses.</summary>
    private const string RestingForeground = "grey70";

    /// <summary>Style of the controls line and of the compose entry's placeholder.</summary>
    private const string HintStyle = "grey54 italic";

    /// <summary>Controls line: a terminal gives no cue that an option can be picked, so the keys are spelled out and the command palette with them.</summary>
    private const string HintText = "↑↓ move · type to write · Enter choose · / commands · Esc quit";

    /// <summary>Id of the compose entry: this channel adds it to the offer and never sends it.</summary>
    private const string ComposeEntryId = "terminal:compose";

    /// <summary>What the compose entry shows while nothing is written in it: the way to ask for what no option offers.</summary>
    private const string ComposePlaceholder = "💬 Ask me something else…";

    /// <summary>Measures and cuts every row to the width the UI's row budget counts on.</summary>
    private readonly TerminalCellService cells;

    /// <summary>Colours the option that closes the branch the conversation is in.</summary>
    private readonly TerminalTheme theme;

    /// <summary>Backing store of <see cref="IsOffering"/>, written under the render lock and read by the key loop without it.</summary>
    private volatile bool isOffering;

    /// <summary>The options of the current turn with the compose entry among them; null when none are on offer.</summary>
    private IReadOnlyList<QuickReply>? offeredReplies;

    /// <summary>Position of the highlighted option among <see cref="offeredReplies"/>.</summary>
    private int highlightedIndex;

    /// <summary>Captures the cell measurement and the channel's theme.</summary>
    public QuickReplyPromptService(TerminalCellService cells, TerminalTheme theme)
    {
        this.cells = cells;
        this.theme = theme;
    }

    /// <summary>Tells whether options are on offer, which makes them the prompt until one is picked, a line is written or the offer is dismissed.</summary>
    public bool IsOffering => isOffering;

    /// <summary>Tells whether the compose entry is highlighted, which gives the keys of the free-text line to the prompt.</summary>
    public bool IsComposing => offeredReplies is { } replies && highlightedIndex < replies.Count && IsComposeEntry(replies[highlightedIndex]);

    /// <summary>
    /// Puts <paramref name="quickReplies"/> on offer with the highlight on the first and the compose entry among them,
    /// right above the first option that hands the conversation back: what keeps the user in the turn comes before what ends it.
    /// </summary>
    public void Offer(IReadOnlyList<QuickReply> quickReplies)
    {
        // The compose entry goes before the way out, or last when the offer has none
        int firstTermination = quickReplies.ToList().FindIndex(reply => reply.Termination == true);
        int composeIndex = firstTermination < 0 ? quickReplies.Count : firstTermination;
        offeredReplies = [.. quickReplies.Take(composeIndex), new QuickReply(ComposeEntryId, ComposePlaceholder, string.Empty), .. quickReplies.Skip(composeIndex)];
        highlightedIndex = 0;
        isOffering = true;
    }

    /// <summary>Moves the highlight to the compose entry, for a printable key that the user typed on another option.</summary>
    public void FocusCompose()
    {
        // The offer may have been dismissed between the key loop's lock-free read and this call
        if (offeredReplies is { } replies)
            highlightedIndex = replies.ToList().FindIndex(IsComposeEntry);
    }

    /// <summary>Takes the offer away, for a turn sent in answer to it or a conversation that no longer branches.</summary>
    public void Dismiss()
    {
        isOffering = false;
        offeredReplies = null;
        highlightedIndex = 0;
    }

    /// <summary>
    /// Answers <paramref name="key"/>: the arrows move the highlight on both axes so the user need not guess how the options are laid out,
    /// Enter picks the highlighted one and every other key means nothing here. The offer stands until
    /// <see cref="Dismiss"/>, since the caller sends the pick before the prompt changes. Null when nothing was picked:
    /// the compose entry is never picked, since what it sends is the line written in it.
    /// </summary>
    public QuickReply? HandleKey(ConsoleKey key)
    {
        // The offer may have been dismissed between the key loop's lock-free read and this call
        if (offeredReplies is not { Count: > 0 } replies)
            return null;

        // Keys not listed here mean nothing to the offer
        switch (key)
        {
            // Up and Left walk towards the first option, wrapping to the last
            case ConsoleKey.UpArrow or ConsoleKey.LeftArrow:
                highlightedIndex = (highlightedIndex - 1 + replies.Count) % replies.Count;
                return null;

            // Down and Right walk towards the last option, wrapping to the first
            case ConsoleKey.DownArrow or ConsoleKey.RightArrow:
                highlightedIndex = (highlightedIndex + 1) % replies.Count;
                return null;

            // Enter picks what is highlighted, an option of the reply and never the compose entry
            case ConsoleKey.Enter:
                return IsComposeEntry(replies[highlightedIndex]) ? null : replies[highlightedIndex];

            // Any other key leaves the offer as it was
            default:
                return null;
        }
    }

    /// <summary>
    /// Draws the offer: the controls line then one option per row, whatever the count. Every row is one markup
    /// of at most <paramref name="width"/> cells. Empty when nothing is on offer.
    /// </summary>
    /// <param name="width">The cells each row may take.</param>
    /// <param name="composeText">The free-text line, drawn in the compose entry.</param>
    /// <param name="composeCursor">The caret's position in <paramref name="composeText"/>, as a char index.</param>
    public List<Markup> RenderPrompt(int width, string composeText, int composeCursor)
    {
        // Nothing on offer draws nothing
        if (offeredReplies is not { } replies)
            return [];

        // A terminal reporting no width still gets one cell per row
        width = Math.Max(1, width);

        // The controls line opens the surface, cell-wrapped so it too keeps to one markup per row
        List<Markup> rows = [.. cells.Wrap(HintText, width).Select(slice => new Markup($"[{HintStyle}]{Markup.Escape(slice)}[/]"))];

        // The options stack vertically, the highlighted one inverted and the compose entry showing the line written in it
        for (int index = 0; index < replies.Count; index++)
            rows.Add(IsComposeEntry(replies[index])
                ? RenderComposeEntry(index == highlightedIndex, composeText, composeCursor, width)
                : RenderOption(replies[index], index == highlightedIndex, width));
        return rows;
    }

    /// <summary>Tells the compose entry apart from the options of the reply.</summary>
    private static bool IsComposeEntry(QuickReply reply)
        => string.Equals(reply.Id, ComposeEntryId, StringComparison.Ordinal);

    /// <summary>
    /// Draws the compose entry as its own row: the placeholder while nothing is written, otherwise the line with its caret
    /// when highlighted. A line wider than the row shows the part around the caret, so what is being typed stays in sight.
    /// </summary>
    private Markup RenderComposeEntry(bool highlighted, string composeText, int composeCursor, int width)
    {
        // The caret column matches the options', so the entry lines up with them
        StringBuilder rowMarkup = new(width + 64);
        rowMarkup.Append(highlighted ? $"[{TerminalTheme.UserColor}]❯[/] " : "  ");

        // An empty line shows the placeholder
        if (composeText.Length == 0)
        {
            // The caret waits before the placeholder once the entry is the one being written in
            if (highlighted)
                rowMarkup.Append($"[blink {TerminalTheme.UserColor}]_[/]");

            // Emoji are resolved and variation selectors dropped before truncating, so the width measured is the width drawn
            string placeholder = cells.Trunc(cells.StripVariationSelectors(Emoji.Replace(ComposePlaceholder)), Math.Max(1, width - 4));

            // The placeholder wears the hint style, so it reads as an invitation rather than as something written
            rowMarkup.Append('[').Append(HintStyle).Append("] ").Append(Markup.Escape(placeholder)).Append("[/]");
            return new Markup(rowMarkup.ToString());
        }

        // Each rune with the cells it takes, the caret drawn as the free-text prompt draws it: inverted on the rune it sits before
        List<(string Markup, int Cells)> units = [];
        int caretUnit = -1;
        int charOffset = 0;
        foreach (Rune rune in composeText.EnumerateRunes())
        {
            // The rune is escaped before it is drawn
            string glyph = Markup.Escape(rune.ToString());

            // The caret is kept as a char index and shows only on the entry being written in
            bool caretHere = highlighted && composeCursor >= charOffset && composeCursor < charOffset + rune.Utf16SequenceLength;
            if (caretHere)
                caretUnit = units.Count;

            // The rune joins the line with the cells it occupies, a wide one counting two
            units.Add((caretHere ? $"[blink {TerminalTheme.UserColor} invert]{glyph}[/]" : glyph, cells.RuneCells(rune)));
            charOffset += rune.Utf16SequenceLength;
        }

        // A caret at the end of the line has no rune to sit on and blinks as an underscore after the last one
        if (highlighted && caretUnit < 0)
        {
            caretUnit = units.Count;
            units.Add(($"[blink {TerminalTheme.UserColor}]_[/]", 1));
        }

        // The window narrows from the end beyond the caret first, then from the start, until it fits the cells left after
        // the caret column and the space that lines the text up with the options' labels
        int available = Math.Max(1, width - 3);
        int windowStart = 0;
        int windowEnd = units.Count;
        int windowCells = units.Sum(unit => unit.Cells);
        while (windowCells > available && windowEnd - windowStart > 1)
        {
            // What lies after the caret goes first, so the runes being typed stay in sight
            if (windowEnd - 1 > caretUnit)
                windowCells -= units[--windowEnd].Cells;
            else
                windowCells -= units[windowStart++].Cells;
        }

        // What the window holds is the row after the caret column, starting where the options' labels start
        rowMarkup.Append(' ');
        for (int index = windowStart; index < windowEnd; index++)
            rowMarkup.Append(units[index].Markup);
        return new Markup(rowMarkup.ToString());
    }

    /// <summary>Draws one option as its own row: a caret then the space-padded label, truncated so caret and padding never push it past <paramref name="width"/>.</summary>
    private Markup RenderOption(QuickReply reply, bool highlighted, int width)
    {
        // Emoji are resolved and variation selectors dropped before truncating, so the width measured is the width drawn.
        // The label comes from the network, so control characters are stripped first
        string label = cells.Trunc(
            cells.StripVariationSelectors(Emoji.Replace(TerminalCellService.StripControlCharacters(reply.Label))),
            Math.Max(1, width - 4));

        // The caret is two columns wide whether lit or not, which keeps the options aligned
        StringBuilder rowMarkup = new(width + 32);
        if (highlighted)
            rowMarkup.Append('[').Append(TerminalTheme.UserColor).Append("]❯[/] ");
        else
            rowMarkup.Append("  ");

        // The label sits in a padded chip styled by its kind and highlight
        rowMarkup.Append('[').Append(ChipStyle(reply, highlighted)).Append("] ").Append(Markup.Escape(label)).Append(" [/]");
        return new Markup(rowMarkup.ToString());
    }

    /// <summary>
    /// The style of an option's label. An ordinary option inverts to dark on the user's colour when highlighted and rests in grey
    /// otherwise. An option that terminates the branch wears the Morgana colour instead, which tells at a glance that picking it hands the conversation back to Morgana.
    /// </summary>
    private string ChipStyle(QuickReply reply, bool highlighted)
    {
        // An option that closes the branch is tinted in Morgana's colour
        if (reply.Termination == true)
            return highlighted ? $"white on {theme.MorganaColor}" : theme.MorganaColor;

        // Any other option follows the user's accent when highlighted
        return highlighted ? $"black on {TerminalTheme.UserColor}" : RestingForeground;
    }
}
