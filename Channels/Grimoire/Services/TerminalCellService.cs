using System.Text;
using Spectre.Console;

namespace Grimoire.Services;

/// <summary>
/// Terminal-cell-width text measurement shared by Grimoire's TTY renderers (Markdown, rich card,
/// quick reply): rune-safe wrap/truncate so a wide CJK glyph or an emoji-presentation sequence
/// (resolved via <see cref="Emoji.Replace"/> upstream in each renderer) is measured in the columns
/// the terminal actually draws, not in UTF-16 chars — keeping every renderer's "one row = exactly
/// width columns" invariant honest, the same contract <c>ConsoleUiService.BuildBody</c> budgets
/// its scrollback window against.
/// </summary>
public sealed class TerminalCellService
{
    // Spectre's Wcwidth table is the source of truth for "how many columns does this glyph
    // actually occupy on screen" — 0 for combining marks/variation selectors, 2 for wide CJK,
    // 1 for everything else. Every other method here is built on top of this single call.
    /// <summary>Terminal cell width of a single rune.</summary>
    public int RuneCells(Rune rune) => rune.ToString().GetCellWidth();

    /// <summary>Greedy word-wrap by terminal cell, not char count: walks the text one rune at a time so a surrogate pair is never split, and always hands back at least one (possibly empty) slice.</summary>
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