using System.Text;
using Spectre.Console;

namespace Morgana.Terminal.Services;

/// <summary>
/// Terminal-safe text shared by every renderer of a TTY channel: wrapping and truncating by the columns the
/// terminal actually draws, so a wide CJK glyph or an emoji-presentation sequence never breaks the contract
/// that one row is exactly the width asked, which <c>TerminalUiService.BuildBody</c> budgets its scrollback window against.
/// </summary>
public sealed class TerminalCellService
{
    /// <summary>
    /// Strips control characters (ESC, BEL, C1 controls) out of a single-line piece of text bound for the terminal.
    /// <see cref="Markup.Escape"/> only neutralizes Spectre's own markup syntax while a raw control byte (an OSC sequence
    /// renaming the terminal title, a cursor move, the bell) is obeyed by the user's TTY. Everything Morgana sends arrives
    /// over an unauthenticated callback, so a speaker name, a quick-reply label and a card field are untrusted until drawn.
    /// </summary>
    public static string StripControlCharacters(string text) =>
        text.Any(char.IsControl) ? new string(text.Where(c => !char.IsControl(c)).ToArray()) : text;

    /// <summary>Terminal cell width of a single rune: 0 for combining marks and variation selectors, 2 for wide CJK, 1 for everything else.</summary>
    public int RuneCells(Rune rune) => rune.ToString().GetCellWidth();

    /// <summary>Breaks text into slices of at most <paramref name="width"/> cells without splitting a rune; always at least one slice, possibly empty.</summary>
    public List<string> Wrap(string text, int width)
    {
        // A terminal reporting no width still gets one cell per slice
        width = Math.Max(1, width);

        // An empty text still occupies one row
        if (text.Length == 0)
            return [string.Empty];

        // The slices closed so far
        List<string> slices = [];
        StringBuilder slice = new();
        int sliceCells = 0;
        foreach (Rune rune in text.EnumerateRunes())
        {
            // The rune is measured in the columns the terminal draws, not in UTF-16 chars
            int runeCells = RuneCells(rune);

            // The next rune would push the slice past the width: the slice is closed and a fresh one opened,
            // unless it is still empty, where a rune wider than the whole width is forced through rather than looping forever
            if (sliceCells + runeCells > width && slice.Length > 0)
            {
                // The closed slice joins the result
                slices.Add(slice.ToString());
                slice.Clear();
                sliceCells = 0;
            }

            // The rune joins the slice being built
            slice.Append(rune.ToString());
            sliceCells += runeCells;
        }

        // Whatever is left is the last slice; a text of nothing but zero-width runes still gives one
        if (slice.Length > 0 || slices.Count == 0)
            slices.Add(slice.ToString());
        return slices;
    }

    /// <summary>Wraps prose at the spaces between words so no row ends mid-word; only a word wider than the row is cut by cell.</summary>
    public List<string> WrapWords(string text, int width)
    {
        // A terminal reporting no width still gets one cell per row
        width = Math.Max(1, width);
        List<string> rows = [];
        StringBuilder row = new();
        int rowCells = 0;
        foreach (string word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            // The word is measured in the columns the terminal draws
            int wordCells = word.GetCellWidth();

            // The word joins the row when it fits after a space
            if (row.Length > 0 && rowCells + 1 + wordCells <= width)
            {
                // The word follows the previous one after a space
                row.Append(' ').Append(word);
                rowCells += 1 + wordCells;
                continue;
            }

            // Otherwise the row is closed and the word opens the next
            if (row.Length > 0)
                rows.Add(row.ToString());
            row.Clear();
            rowCells = 0;

            // A word no row can hold is cut by cell, its last slice left open for the words after it
            List<string> slices = wordCells > width ? Wrap(word, width) : [word];
            rows.AddRange(slices.Take(slices.Count - 1));
            row.Append(slices[^1]);
            rowCells = slices[^1].GetCellWidth();
        }

        // The last row holds the words left; a text of no words gives one empty row
        if (row.Length > 0 || rows.Count == 0)
            rows.Add(row.ToString());
        return rows;
    }

    /// <summary>The rule that opens a command's panel: its title on a line drawn across the whole row in <paramref name="color"/>.</summary>
    public Markup RenderPanelTitle(string title, int width, string color)
    {
        // The title is followed by a line filling what the row has left
        string titleLead = $"── {title} ";
        return new Markup($"[{color}]{Markup.Escape(Trunc(titleLead + new string('─', Math.Max(0, width - titleLead.GetCellWidth())), width))}[/]");
    }

    /// <summary>Cuts text down to <paramref name="width"/> terminal cells, tacking on an ellipsis when it had to cut something.</summary>
    public string Trunc(string text, int width)
    {
        // A terminal reporting no width still gets one cell
        width = Math.Max(1, width);

        // Text that already fits is left alone
        if (text.GetCellWidth() <= width)
            return text;

        // One column is kept for the ellipsis, except at width 1 where there is room for the content or the ellipsis and not both
        int budget = width >= 2 ? width - 1 : width;
        StringBuilder keptText = new();
        int keptCells = 0;
        foreach (Rune rune in text.EnumerateRunes())
        {
            // The rune is measured in the columns the terminal draws
            int runeCells = RuneCells(rune);

            // A rune that would blow the budget ends the cut and is never split
            if (keptCells + runeCells > budget)
                break;
            keptText.Append(rune.ToString());
            keptCells += runeCells;
        }

        // The ellipsis tells the user that the text goes on
        return width >= 2 ? keptText + "…" : keptText.ToString();
    }

    /// <summary>
    /// Removes the variation selectors (U+FE00 to U+FE0F) that flip a glyph between text and emoji presentation.
    /// Spectre measures an emoji-presentation sequence such as a warning sign as two cells while most terminals draw
    /// one, so dropping the selector keeps the width measured equal to the width drawn.
    /// </summary>
    public string StripVariationSelectors(string text)
    {
        // Almost no text has a selector, so the common case allocates nothing
        bool hasSelector = false;
        foreach (char character in text)
            if (character is >= '\uFE00' and <= '\uFE0F')
            {
                // One selector is enough to need the slow path
                hasSelector = true;
                break;
            }
        // No selector: the text is returned as it is
        if (!hasSelector)
            return text;

        // All selectors are single BMP chars, so a char-level scan cannot cut a surrogate pair
        StringBuilder textWithoutSelectors = new(text.Length);
        foreach (char character in text)
        {
            // Everything but a selector is kept
            if (character is not (>= '\uFE00' and <= '\uFE0F'))
                textWithoutSelectors.Append(character);
        }
        // The text without its selectors
        return textWithoutSelectors.ToString();
    }
}
