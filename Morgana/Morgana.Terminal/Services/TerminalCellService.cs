using System.Text;
using Spectre.Console;

namespace Morgana.Terminal.Services;

/// <summary>
/// Terminal-safe text shared by every TTY renderer a channel owns (prose, plus the rich card and
/// quick reply of a channel that declares them): rune-safe wrap/truncate so a wide CJK glyph or an emoji-presentation sequence
/// (resolved via <see cref="Emoji.Replace"/> upstream in each renderer) is measured in the columns
/// the terminal actually draws, not in UTF-16 chars — keeping every renderer's "one row = exactly
/// width columns" invariant honest, the same contract <c>ConsoleUiService.BuildBody</c> budgets
/// its scrollback window against.
/// </summary>
public sealed class TerminalCellService
{
    /// <summary>
    /// Strips ASCII/Unicode control characters (ESC, BEL, C1 controls, ...) out of a single-line piece
    /// of text bound for the terminal. <see cref="Markup.Escape"/> only neutralizes Spectre's own
    /// <c>[ ]</c> markup syntax — a raw control byte (an OSC sequence renaming the terminal title, a
    /// cursor move, the BEL that rings the system bell) passes straight through it and is obeyed by the
    /// user's TTY. Everything Morgana sends arrives over an unauthenticated callback, so a speaker name,
    /// a quick-reply label and a card field are all untrusted the moment they are about to be drawn.
    /// </summary>
    public static string StripControlCharacters(string text) =>
        text.Any(char.IsControl) ? new string(text.Where(c => !char.IsControl(c)).ToArray()) : text;

    // Spectre's Wcwidth table is the source of truth for "how many columns does this glyph
    // actually occupy on screen" — 0 for combining marks/variation selectors, 2 for wide CJK,
    // 1 for everything else. Every other method here is built on top of this single call.
    /// <summary>Terminal cell width of a single rune.</summary>
    public int RuneCells(Rune rune) => rune.ToString().GetCellWidth();

    /// <summary>Greedy word-wrap by terminal cell, not char count: walks the text one rune at a time so a surrogate pair is never split and always hands back at least one (possibly empty) slice.</summary>
    public List<string> Wrap(string text, int width)
    {
        width = Math.Max(1, width);
        if (text.Length == 0)
            return [string.Empty];

        List<string> slices = [];
        StringBuilder current = new();
        int currentCells = 0;
        foreach (Rune rune in text.EnumerateRunes())
        {
            int runeCells = RuneCells(rune);
            // The next rune would push this slice past width: close it off and start a fresh one —
            // unless the slice is still empty, in which case a single rune wider than the whole
            // width (pathological) gets forced through anyway rather than looping forever.
            if (currentCells + runeCells > width && current.Length > 0)
            {
                slices.Add(current.ToString());
                current.Clear();
                currentCells = 0;
            }
            current.Append(rune.ToString());
            currentCells += runeCells;
        }
        // Flush whatever's left, or hand back one empty slice so callers always get at least a row.
        if (current.Length > 0 || slices.Count == 0)
            slices.Add(current.ToString());
        return slices;
    }

    /// <summary>Wraps prose at the spaces between words, so no row ends mid-word; only a word wider than the row is cut by cell.</summary>
    public List<string> WrapWords(string text, int width)
    {
        width = Math.Max(1, width);
        List<string> rows = [];
        StringBuilder current = new();
        int currentCells = 0;
        foreach (string word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int wordCells = word.GetCellWidth();

            // The word joins the row when it fits after a space; otherwise the row is closed and the word opens the next
            if (current.Length > 0 && currentCells + 1 + wordCells <= width)
            {
                current.Append(' ').Append(word);
                currentCells += 1 + wordCells;
                continue;
            }

            if (current.Length > 0)
                rows.Add(current.ToString());
            current.Clear();
            currentCells = 0;

            // A word no row can hold is cut by cell, its last slice left open for the words after it
            List<string> slices = wordCells > width ? Wrap(word, width) : [word];
            rows.AddRange(slices.Take(slices.Count - 1));
            current.Append(slices[^1]);
            currentCells = slices[^1].GetCellWidth();
        }

        if (current.Length > 0 || rows.Count == 0)
            rows.Add(current.ToString());
        return rows;
    }

    /// <summary>Cuts text down to fit width terminal cells, tacking on an ellipsis when it actually had to cut something.</summary>
    public string Trunc(string text, int width)
    {
        width = Math.Max(1, width);
        if (text.GetCellWidth() <= width)
            return text; // already fits, nothing to do

        // Reserve one column for the "…" itself (unless width is 1, where there's no room for
        // both content and ellipsis — then just hard-cut to that single column).
        int budget = width >= 2 ? width - 1 : width;
        StringBuilder sb = new();
        int cells = 0;
        foreach (Rune rune in text.EnumerateRunes())
        {
            int runeCells = RuneCells(rune);
            if (cells + runeCells > budget)
                break; // this rune would blow the budget — stop here, don't split it
            sb.Append(rune.ToString());
            cells += runeCells;
        }
        return width >= 2 ? sb + "…" : sb.ToString();
    }

    /// <summary>
    /// Removes Unicode variation selectors (U+FE00–U+FE0F): zero-width format codepoints that flip
    /// a base glyph between text and emoji presentation. An emoji-presentation sequence such as
    /// <c>⚠️</c> (<c>⚠</c> + U+FE0F) is measured as two cells by Wcwidth yet rendered as one by most
    /// terminals — stripping the selector keeps measured and rendered widths in agreement. All
    /// selectors are single BMP chars, so a char-level scan is surrogate-safe.
    /// </summary>
    public string StripVariationSelectors(string text)
    {
        bool hasSelector = false;
        foreach (char c in text)
            if (c is >= '\uFE00' and <= '\uFE0F')
            {
                hasSelector = true;
                break;
            }
        if (!hasSelector)
            return text; // hot path: the overwhelming majority of text has none

        StringBuilder sb = new(text.Length);
        foreach (char c in text)
        {
            if (c is not (>= '\uFE00' and <= '\uFE0F'))
                sb.Append(c);
        }
        return sb.ToString();
    }
}