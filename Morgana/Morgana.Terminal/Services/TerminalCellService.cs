using System.Text;
using Spectre.Console;

namespace Morgana.Terminal.Services;

/// <summary>Terminal-safe text shared by every renderer of a TTY channel, measured in the columns the terminal draws.</summary>
// A wide CJK glyph or an emoji-presentation sequence must never break the contract that one row is exactly the width asked,
// which TerminalUiService budgets its scrollback window against.
public sealed class TerminalCellService
{
    /// <summary>Strips control characters, ESC, BEL and the C1 controls among them, out of a single-line text bound for the terminal.</summary>
    // Markup.Escape neutralises Spectre's own syntax only, while a raw control byte (an OSC sequence renaming the terminal, a
    // cursor move, the bell) is obeyed by the user's TTY. Everything Morgana sends arrives over an unauthenticated callback,
    // so a speaker name, a quick-reply label and a card field are untrusted until drawn.
    public static string StripControlCharacters(string text) =>
        text.Any(char.IsControl) ? new string(text.Where(character => !char.IsControl(character)).ToArray()) : text;

    /// <summary>Prepares a text published by Morgana for one row of the terminal: emoji resolved, variation selectors and control characters dropped.</summary>
    // Emoji are resolved and selectors dropped before measuring, so the width measured is the width drawn.
    public string SanitizeForTerminal(string text) =>
        StripControlCharacters(StripVariationSelectors(Emoji.Replace(text)));

    /// <summary>The cells a single rune takes: 0 for combining marks and variation selectors, 2 for wide CJK, 1 for everything else.</summary>
    public int RuneCells(Rune rune) => rune.ToString().GetCellWidth();

    /// <summary>Breaks text into slices of at most <paramref name="width"/> cells without splitting a rune, always at least one slice.</summary>
    public List<string> Wrap(string text, int width)
    {
        // A terminal reporting no width still gets one cell per slice.
        width = Math.Max(1, width);

        // An empty text still occupies one row.
        if (text.Length == 0)
            return [string.Empty];

        // The slices closed so far and the one being filled.
        List<string> slices = [];
        StringBuilder slice = new();
        int sliceCells = 0;

        // The text is walked rune by rune, so a surrogate pair is never cut.
        foreach (Rune rune in text.EnumerateRunes())
        {
            // The rune is measured in the columns the terminal draws, not in UTF-16 chars.
            int runeCells = RuneCells(rune);

            // A rune that would push the slice past the width closes it, unless it is still empty: a rune wider than the
            // whole width is forced through rather than looping forever.
            if (sliceCells + runeCells > width && slice.Length > 0)
            {
                // The closed slice joins the result.
                slices.Add(slice.ToString());
                slice.Clear();
                sliceCells = 0;
            }

            // The rune joins the slice being built.
            slice.Append(rune.ToString());
            sliceCells += runeCells;
        }

        // Whatever is left is the last slice; a text of nothing but zero-width runes still gives one.
        if (slice.Length > 0 || slices.Count == 0)
            slices.Add(slice.ToString());

        // Every slice in reading order.
        return slices;
    }

    /// <summary>Wraps prose at the spaces between words so that no row ends mid-word, cutting by cell only a word wider than the row.</summary>
    public List<string> WrapWords(string text, int width)
    {
        // A terminal reporting no width still gets one cell per row.
        width = Math.Max(1, width);

        // The rows closed so far and the one being filled.
        List<string> rows = [];
        StringBuilder row = new();
        int rowCells = 0;

        // Words are placed one at a time, the runs of spaces between them collapsing to one.
        foreach (string word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            // The word is measured in the columns the terminal draws.
            int wordCells = word.GetCellWidth();

            // The word joins the row when it fits after a space.
            if (row.Length > 0 && rowCells + 1 + wordCells <= width)
            {
                // The word follows the previous one after a space.
                row.Append(' ').Append(word);
                rowCells += 1 + wordCells;
                continue;
            }

            // Otherwise the row is closed and the word opens the next.
            if (row.Length > 0)
                rows.Add(row.ToString());
            row.Clear();
            rowCells = 0;

            // A word no row can hold is cut by cell, its last slice left open for the words after it.
            List<string> slices = wordCells > width ? Wrap(word, width) : [word];
            rows.AddRange(slices.Take(slices.Count - 1));
            row.Append(slices[^1]);
            rowCells = slices[^1].GetCellWidth();
        }

        // The last row holds the words left; a text of no words gives one empty row.
        if (row.Length > 0 || rows.Count == 0)
            rows.Add(row.ToString());

        // Every row in reading order.
        return rows;
    }

    /// <summary>The rule that opens a command's panel: its title on a line drawn across the whole row in <paramref name="color"/>.</summary>
    public Markup RenderPanelTitle(string title, int width, string color)
    {
        // The title is followed by a line filling what the row has left.
        string titleLead = $"── {title} ";
        return new Markup($"[{color}]{Markup.Escape(Trunc(titleLead + new string('─', Math.Max(0, width - titleLead.GetCellWidth())), width))}[/]");
    }

    /// <summary>Cuts text down to <paramref name="width"/> terminal cells, closing it with an ellipsis when something was cut.</summary>
    public string Trunc(string text, int width)
    {
        // A terminal reporting no width still gets one cell.
        width = Math.Max(1, width);

        // Text that already fits is left alone.
        if (text.GetCellWidth() <= width)
            return text;

        // One column is kept for the ellipsis, except at width 1 where there is room for the content or the ellipsis and not both.
        int budget = width >= 2 ? width - 1 : width;
        StringBuilder keptText = new();
        int keptCells = 0;

        // Runes are kept from the start of the text for as long as they fit.
        foreach (Rune rune in text.EnumerateRunes())
        {
            // The rune is measured in the columns the terminal draws.
            int runeCells = RuneCells(rune);

            // A rune that would blow the budget ends the cut and is never split.
            if (keptCells + runeCells > budget)
                break;

            // The rune fits and is kept.
            keptText.Append(rune.ToString());
            keptCells += runeCells;
        }

        // The ellipsis tells the user that the text goes on.
        return width >= 2 ? keptText + "…" : keptText.ToString();
    }

    /// <summary>Removes the variation selectors, U+FE00 to U+FE0F, that flip a glyph between text and emoji presentation.</summary>
    // Spectre measures an emoji-presentation sequence such as a warning sign as two cells while most terminals draw one, so
    // dropping the selector keeps the width measured equal to the width drawn.
    public string StripVariationSelectors(string text)
    {
        // Almost no text has a selector, so the common case allocates nothing.
        if (text.AsSpan().IndexOfAnyInRange('\uFE00', '\uFE0F') < 0)
            return text;

        // Every selector is a single BMP char, so a char-level scan cannot cut a surrogate pair.
        StringBuilder textWithoutSelectors = new(text.Length);
        foreach (char character in text)
        {
            // Everything but a selector is kept.
            if (character is not (>= '\uFE00' and <= '\uFE0F'))
                textWithoutSelectors.Append(character);
        }

        // The text without its selectors.
        return textWithoutSelectors.ToString();
    }
}