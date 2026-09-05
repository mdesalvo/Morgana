using System.Text;
using Spectre.Console;

namespace Rune.Services;

/// <summary>
/// Terminal-cell-width text measurement: rune-safe wrap so a wide CJK glyph or an
/// emoji-presentation sequence is measured in the columns the terminal actually draws, not in
/// UTF-16 chars — keeping <c>ConsoleUiService</c>'s "one row = exactly width columns" invariant
/// honest, the same contract its scrollback window budgets against.
/// </summary>
public sealed class TerminalCellService
{
    // Spectre's Wcwidth table is the source of truth for "how many columns does this glyph
    // actually occupy on screen" — 0 for combining marks/variation selectors, 2 for wide CJK,
    // 1 for everything else. Wrap and StripVariationSelectors below are both built on this call.
    /// <summary>Terminal cell width of a single rune.</summary>
    public int RuneCells(System.Text.Rune rune) => rune.ToString().GetCellWidth();

    /// <summary>Greedy word-wrap by terminal cell, not char count: walks the text one rune at a time so a surrogate pair is never split and always hands back at least one (possibly empty) slice.</summary>
    public List<string> Wrap(string text, int width)
    {
        width = Math.Max(1, width);
        if (text.Length == 0)
            return [string.Empty];

        List<string> slices = [];
        StringBuilder current = new();
        int currentCells = 0;
        foreach (System.Text.Rune rune in text.EnumerateRunes())
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
            if (c is >= '\uFE00' and <= '\uFE0F') { hasSelector = true; break; }
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