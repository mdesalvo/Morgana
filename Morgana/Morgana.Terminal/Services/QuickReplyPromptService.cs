using System.Text;
using Morgana.Contracts;
using Morgana.Terminal.Messages;
using Spectre.Console;

namespace Morgana.Terminal.Services;

/// <summary>The options a reply offers, as a channel that declares quick replies shows them in place of the prompt.</summary>
// While they are on offer they are the prompt of the turn, so the text input is suspended. The key loop reads the offer
// without a lock through IsOffering, while every other call comes under the render lock of the UI that owns it.
public sealed class QuickReplyPromptService
{
    /// <summary>The foreground of the options that are not highlighted, the dimmed grey the rich card body uses.</summary>
    private const string RestingForeground = "grey70";

    /// <summary>The style of the controls line.</summary>
    private const string HintStyle = "grey54 italic";

    /// <summary>The controls line, which names the command palette too, since a terminal gives no cue that an option can be picked.</summary>
    private const string HintText = "↑↓ move · Enter choose · / commands · Esc quit";

    /// <summary>Measures and cuts every row to the width that the UI's row budget counts on.</summary>
    private readonly TerminalCellService cells;

    /// <summary>Colours the option that closes the branch the conversation is in.</summary>
    private readonly TerminalTheme theme;

    /// <summary>Backing store of <see cref="IsOffering"/>, written under the render lock and read by the key loop without it.</summary>
    private volatile bool isOffering;

    /// <summary>The options of the current turn; null when none are on offer.</summary>
    private IReadOnlyList<QuickReply>? offeredReplies;

    /// <summary>The position of the highlighted option among <see cref="offeredReplies"/>.</summary>
    private int highlightedIndex;

    /// <summary>Builds the offer on the cell measurement and the channel's theme.</summary>
    public QuickReplyPromptService(TerminalCellService cells, TerminalTheme theme)
    {
        // The offer measures its rows with the UI's own cells and paints them in the channel's theme.
        this.cells = cells;
        this.theme = theme;
    }

    /// <summary>Tells whether options are on offer, which suspends the text input until one is picked or the offer is dismissed.</summary>
    public bool IsOffering => isOffering;

    /// <summary>Puts <paramref name="quickReplies"/> on offer with the highlight on the first.</summary>
    public void Offer(IReadOnlyList<QuickReply> quickReplies)
    {
        // The flag goes up last, so the key loop never sees an offer whose options are not set yet.
        offeredReplies = quickReplies;
        highlightedIndex = 0;
        isOffering = true;
    }

    /// <summary>Takes the offer away, for a turn sent in answer to it or a conversation that no longer branches.</summary>
    public void Dismiss()
    {
        // The flag goes down first, so the key loop stops treating keys as picks before the options are gone.
        isOffering = false;
        offeredReplies = null;
        highlightedIndex = 0;
    }

    /// <summary>The option that <paramref name="key"/> picks, Enter on the highlighted one; null for any other key, the arrows included, which move the highlight.</summary>
    // The arrows work on both axes so the user need not guess how the options are laid out. The offer stands until Dismiss,
    // since the caller sends the pick before the prompt changes.
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

    /// <summary>The offer as rows of at most <paramref name="width"/> cells: the controls line, then one option per row; empty when nothing is on offer.</summary>
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

        // The controls line and the options, top to bottom.
        return rows;
    }

    /// <summary>One option as its own row: a caret, then the padded label, truncated so that caret and padding never push it past <paramref name="width"/>.</summary>
    private Markup RenderOption(QuickReply reply, bool highlighted, int width)
    {
        // The label comes from the network, so it is cleaned before it is measured and cut.
        string label = cells.Trunc(cells.SanitizeForTerminal(reply.Label), Math.Max(1, width - 4));

        // The caret is two columns wide whether lit or not, which keeps the options aligned
        StringBuilder rowMarkup = new(width + 32);
        if (highlighted)
            rowMarkup.Append('[').Append(TerminalTheme.UserColor).Append("]❯[/] ");
        else
            rowMarkup.Append("  ");

        // The label sits in a padded chip styled by its kind and highlight
        rowMarkup.Append('[').Append(ChipStyle(reply, highlighted)).Append("] ").Append(Markup.Escape(label)).Append(" [/]");

        // The option as one markup of at most the width.
        return new Markup(rowMarkup.ToString());
    }

    /// <summary>The style of an option's label: Morgana's colour for one that hands the conversation back, the user's colour or grey otherwise.</summary>
    // An ordinary option inverts to dark on the user's colour when highlighted and rests in grey otherwise; one that terminates
    // the branch wears the Morgana colour instead, which tells at a glance where picking it leads.
    private string ChipStyle(QuickReply reply, bool highlighted)
    {
        // An option that closes the branch is tinted in Morgana's colour
        if (reply.Termination == true)
            return highlighted ? $"white on {theme.MorganaColor}" : theme.MorganaColor;

        // Any other option follows the user's accent when highlighted
        return highlighted ? $"black on {TerminalTheme.UserColor}" : RestingForeground;
    }
}