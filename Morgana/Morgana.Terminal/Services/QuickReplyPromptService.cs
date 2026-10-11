using System.Text;
using Morgana.Contracts;
using Morgana.Terminal.Messages;
using Spectre.Console;

namespace Morgana.Terminal.Services;

/// <summary>
/// The options a reply offers, as a channel that declares quick replies shows them: which are on offer, which is
/// highlighted, what the keys mean and how they are drawn. While they are on offer they are the prompt of the
/// turn, so the text input is suspended. The offer is read by the key loop without a lock through
/// <see cref="IsOffering"/>, every other call must come under the render lock of the UI that owns it.
/// </summary>
public sealed class QuickReplyPromptService
{
    /// <summary>Foreground for the options that are not highlighted, the de-emphasised grey the rich card body uses.</summary>
    private const string RestingForeground = "grey70";

    /// <summary>Style of the controls line.</summary>
    private const string HintStyle = "grey54 italic";

    /// <summary>Controls line: a terminal gives no cue that an option can be picked, so the keys are spelled out and the command palette with them.</summary>
    private const string HintText = "↑↓ move · Enter choose · / commands · Esc quit";

    /// <summary>Measures and cuts every row to the width the UI's row budget counts on.</summary>
    private readonly TerminalCellService cells;

    /// <summary>Colours the option that closes the branch the conversation is in.</summary>
    private readonly TerminalTheme theme;

    /// <summary>Backing store of <see cref="IsOffering"/>, written under the render lock and read by the key loop without it.</summary>
    private volatile bool isOffering;

    /// <summary>The options of the current turn; null when none are on offer.</summary>
    private IReadOnlyList<QuickReply>? offeredReplies;

    /// <summary>Position of the highlighted option among <see cref="offeredReplies"/>.</summary>
    private int highlightedIndex;

    /// <summary>Captures the cell measurement and the channel's theme.</summary>
    public QuickReplyPromptService(TerminalCellService cells, TerminalTheme theme)
    {
        this.cells = cells;
        this.theme = theme;
    }

    /// <summary>Tells whether options are on offer, which suspends the text input until one is picked or the offer is dismissed.</summary>
    public bool IsOffering => isOffering;

    /// <summary>Puts <paramref name="quickReplies"/> on offer with the highlight on the first.</summary>
    public void Offer(IReadOnlyList<QuickReply> quickReplies)
    {
        offeredReplies = quickReplies;
        highlightedIndex = 0;
        isOffering = true;
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
    /// <see cref="Dismiss"/>, since the caller sends the pick before the prompt changes. Null when nothing was picked.
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

            // Enter picks what is highlighted
            case ConsoleKey.Enter:
                return replies[highlightedIndex];

            // Any other key leaves the offer as it was
            default:
                return null;
        }
    }

    /// <summary>
    /// Draws the offer: the controls line then one option per row, whatever the count. Every row is one markup
    /// of at most <paramref name="width"/> cells. Empty when nothing is on offer.
    /// </summary>
    public List<Markup> RenderPrompt(int width)
    {
        // Nothing on offer draws nothing
        if (offeredReplies is not { } replies)
            return [];

        // A terminal reporting no width still gets one cell per row
        width = Math.Max(1, width);

        // The controls line opens the surface, cell-wrapped so it too keeps to one markup per row
        List<Markup> rows = [.. cells.Wrap(HintText, width).Select(slice => new Markup($"[{HintStyle}]{Markup.Escape(slice)}[/]"))];

        // The options stack vertically, the highlighted one inverted
        for (int index = 0; index < replies.Count; index++)
            rows.Add(RenderOption(replies[index], index == highlightedIndex, width));
        return rows;
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
