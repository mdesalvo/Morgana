using System.Text;
using Morgana.Contracts;
using Markdig;
using Spectre.Console;

namespace Grimoire.Services;

/// <summary>
/// Terminal renderer: Spectrizes RichCard into single-row Markups. Hand-draws bordered box
/// instead of variable-height IRenderables. Card text fields flattened through Markdown.ToPlainText
/// (no inline markdown in terminal, unlike Cauldron's browser HTML formatting).
/// </summary>
public sealed class RichCardTerminalRenderService
{
    /// <summary>Shared default pipeline — same base configuration as Cauldron's <c>MarkdownRendererService</c>, here feeding <c>Markdown.ToPlainText</c> to flatten card text for the terminal.</summary>
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().Build();

    /// <summary>Cell-width measurement this renderer wraps/truncates against.</summary>
    private readonly TerminalCellService cells;

    public RichCardTerminalRenderService(TerminalCellService cells)
    {
        this.cells = cells;
    }

    /// <summary>Body prose foreground — Cauldron's <c>#d0d0d0</c> neutral, distinct from speaker-tinted chat prose.</summary>
    private const string BodyForeground = "grey85";

    /// <summary>De-emphasised foreground for subtitles, keys, muted/small text and captions — Cauldron's <c>~0.7</c> opacity greys.</summary>
    private const string MutedForeground = "grey70";

    /// <summary>Foreground for values and grid cell values — Cauldron's brightest body white <c>#e0e0e0</c>.</summary>
    private const string ValueForeground = "grey93";

    /// <summary>Style of the inner divider fill line.</summary>
    private const string DividerStyle = "grey50";

    /// <summary>
    /// Renders <paramref name="richCard"/> into single-row markups forming a bordered box.
    /// The chrome (border, title, list markers, section titles) is tinted with the speaker's
    /// <paramref name="baseColor"/> so the richCard visibly belongs to whoever sent it, while the
    /// body keeps Cauldron's neutral grey ramp for legibility. <paramref name="width"/> is the
    /// full terminal width the box may occupy.
    /// </summary>
    public List<Markup> RenderRichCard(RichCard richCard, string baseColor, int width)
    {
        // Geometry. The box owns the full terminal width; each interior content row is
        // `│␣ … ␣│` — two border glyphs plus one padding space on each side — so the text
        // actually available is innerWidth = cardWidth - 4. Everything downstream is built and
        // truncated to innerWidth; the framing step pads each line back up to exactly that, which
        // is what keeps every emitted Markup precisely one terminal row wide (the invariant
        // ConsoleUiService.BuildBody budgets against). The Math.Max(8,…) floor stops a pathological
        // narrow terminal from producing a negative innerWidth.
        int cardWidth = Math.Max(8, width);
        int innerWidth = cardWidth - 4; // │ + space + content + space + │

        // PHASE 1 — build "logical" lines (CardLine), independent of the border.
        // Header = the always-present title (bold, speaker-tinted) plus an optional subtitle,
        // each already wrapped/truncated to innerWidth so phase 2 never has to re-measure.
        List<CardLine> header =
        [
            Content([new CardSeg(Trunc(Plain(richCard.Title), innerWidth), $"{baseColor} bold")])
        ];
        if (!string.IsNullOrWhiteSpace(richCard.Subtitle))
        {
            foreach (string slice in WrapText(Plain(richCard.Subtitle), innerWidth))
                header.Add(Content([new CardSeg(slice, MutedForeground)]));
        }

        // The body is the component tree flattened to logical lines (recursively, for sections).
        List<CardLine> body = BuildComponents(richCard.Components, baseColor, innerWidth);

        // PHASE 2 — frame the logical lines into actual rows. Border rows are whole-width fills;
        // content rows go through FrameLine, which sandwiches each CardLine between the side
        // borders and pads it to innerWidth. The header/body split is drawn as ╭─╮ … ├─┤ … ╰─╯;
        // the ├─┤ separator (and the body block) is emitted only when there is a body, so a
        // title-only richCard collapses to a tidy two-border box.
        List<Markup> rows =
        [
            Border($"╭{new string('─', cardWidth - 2)}╮", baseColor)
        ];
        foreach (CardLine line in header)
            rows.Add(FrameLine(line, innerWidth, baseColor));
        if (body.Count > 0)
        {
            rows.Add(Border($"├{new string('─', cardWidth - 2)}┤", baseColor));
            foreach (CardLine line in body)
                rows.Add(FrameLine(line, innerWidth, baseColor));
        }
        rows.Add(Border($"╰{new string('─', cardWidth - 2)}╯", baseColor));
        return rows;
    }

    // ---- component walking --------------------------------------------------------------

    /// <summary>Walks a component list into logical card lines, inserting a blank breather between consecutive cardComponents (no leading/trailing blank) to mirror Cauldron's margins.</summary>
    private List<CardLine> BuildComponents(IEnumerable<CardComponent> cardComponents, string baseColor, int width)
    {
        List<CardLine> output = [];
        bool first = true;
        foreach (CardComponent cardComponent in cardComponents)
        {
            List<CardLine> rendered = BuildComponent(cardComponent, baseColor, width);
            // An unknown/empty component contributes nothing — and must NOT trigger a breather,
            // otherwise we'd leave a stray blank with no content beside it. So the `first` latch
            // only flips once something real has been emitted.
            if (rendered.Count == 0)
                continue;
            if (!first)
                output.Add(Blank());
            output.AddRange(rendered);
            first = false;
        }
        return output;
    }

    private List<CardLine> BuildComponent(CardComponent component, string baseColor, int width) => component switch
    {
        TextBlockComponent textBlock => BuildTextBlock(textBlock, width),
        KeyValueComponent keyValue => BuildKeyValue(keyValue, width),
        DividerComponent => [new CardLine([], IsDivider: true)],
        ListComponent list => BuildList(list, baseColor, width),
        SectionComponent section => BuildSection(section, baseColor, width),
        GridComponent grid => BuildGrid(grid, width),
        BadgeComponent badge => BuildBadge(badge, width),
        ImageComponent image => BuildImage(image, width),
        _ => []
    };

    private List<CardLine> BuildTextBlock(TextBlockComponent textBlock, int width)
    {
        // Map the four declared text styles onto what a TTY can actually express. Bold is real;
        // Muted and Small both collapse to the dim grey — a terminal can't shrink a glyph, so
        // "small" degrades to the same de-emphasis as "muted" rather than being faked with art.
        string style = textBlock.Style switch
        {
            TextStyle.Bold => $"{BodyForeground} bold",
            TextStyle.Muted or TextStyle.Small => MutedForeground,
            _ => BodyForeground
        };
        // Wrap the (plain-text) content to the content width, then turn each wrapped slice into
        // its own single-style CardLine — one logical line per visual row.
        return [.. WrapText(Plain(textBlock.Content), width).Select(slice => Content([new CardSeg(slice, style)]))];
    }

    private List<CardLine> BuildKeyValue(KeyValueComponent keyValue, int width)
    {
        string key = Plain(keyValue.Key);
        string value = Plain(keyValue.Value);
        string keyStyle = keyValue.Emphasize ? $"{MutedForeground} bold" : MutedForeground;
        string valueStyle = keyValue.Emphasize ? $"{ValueForeground} bold" : ValueForeground;

        // Single-row layout when "key" + at least one gap + "value" fits the content width
        // (the +1 is that minimum one-space gap). Widths are measured in terminal cells, not chars,
        // so wide/zero-width glyphs don't skew the fill. The middle segment is pure filler spaces
        // that push the value flush to the right edge: key…………value. fill is guaranteed ≥ 1 here
        // because the branch condition reserved that one column, so the value never abuts the key.
        int keyCells = key.GetCellWidth();
        int valueCells = value.GetCellWidth();
        if (keyCells + valueCells + 1 <= width)
        {
            int fill = width - keyCells - valueCells;
            return [Content([new CardSeg(key, keyStyle), new CardSeg(new string(' ', fill), ""), new CardSeg(value, valueStyle)])];
        }

        // Doesn't fit on one row: stack the key over the value, both wrapped to the content width
        // and left-aligned. We WRAP rather than truncate so a long prose value (e.g. a profile
        // "Details" field) is preserved in full instead of being cut with an ellipsis — each
        // wrapped slice becomes its own single-row CardLine. Key (dim) and value (bright) stay
        // visually distinct by colour alone, so no indentation is needed.
        List<CardLine> lines = [.. WrapText(key, width).Select(slice => Content([new CardSeg(slice, keyStyle)]))];
        lines.AddRange(WrapText(value, width).Select(slice => Content([new CardSeg(slice, valueStyle)])));
        return lines;
    }

    private List<CardLine> BuildList(ListComponent list, string baseColor, int width)
    {
        List<CardLine> output = [];
        int ordinal = 1; // only consumed by the Numbered style, but advanced every item to stay in lockstep
        foreach (string rawItem in list.Items)
        {
            // The marker sits in the speaker colour to read as chrome; "✦" matches Cauldron's
            // bullet glyph. `pad` is a same-width run of spaces used to align continuation rows.
            string marker = list.Style switch
            {
                ListStyle.Numbered => $"{ordinal}. ",
                ListStyle.Plain => string.Empty,
                _ => "✦ "
            };
            ordinal++;
            string pad = new(' ', marker.Length);
            // Wrap the item text in the space LEFT OF the marker so a long item hangs under its
            // own text, not under the bullet: the first wrapped row gets the marker, the rest get
            // the equal-width pad — that's the "hanging indent".
            int textWidth = Math.Max(1, width - marker.Length);
            List<string> wrapped = WrapText(Plain(rawItem), textWidth);
            for (int i = 0; i < wrapped.Count; i++)
            {
                CardSeg prefix = new(i == 0 ? marker : pad, baseColor);
                output.Add(Content([prefix, new CardSeg(wrapped[i], BodyForeground)]));
            }
        }
        return output;
    }

    private List<CardLine> BuildSection(SectionComponent section, string baseColor, int width)
    {
        // No leading blank here: BuildComponents already inserts a breather before every
        // non-first component, so a self-blank would double the gap ahead of a section.
        List<CardLine> output = [];
        foreach (string slice in WrapText(Plain(section.Title), width))
            output.Add(Content([new CardSeg(slice, $"{baseColor} bold")]));
        if (!string.IsNullOrWhiteSpace(section.Subtitle))
            foreach (string slice in WrapText(Plain(section.Subtitle), width))
                output.Add(Content([new CardSeg(slice, MutedForeground)]));

        // Children indent two columns; nested sections recurse and indent further. Built at the
        // reduced width so the two-space prefix never pushes a child line past the content edge.
        const string indent = "  ";
        int childWidth = Math.Max(1, width - indent.Length);
        foreach (CardLine child in BuildComponents(section.Components, baseColor, childWidth))
        {
            // A divider carries no segments — its rule is drawn by FrameLine at full innerWidth —
            // so prefixing it with spaces would do nothing useful; pass it through untouched.
            // Every other child line is re-emitted with a leading two-space segment: that's the
            // visual nesting, and it composes for nested sections (each level adds another indent).
            if (child.IsDivider)
            {
                output.Add(child);
                continue;
            }
            List<CardSeg> indented = [new CardSeg(indent, ""), .. child.Segs];
            output.Add(new CardLine(indented, IsDivider: false));
        }
        return output;
    }

    private List<CardLine> BuildGrid(GridComponent grid, int width)
    {
        // Clamp the requested column count: at least 1, and at most width/2 so every cell keeps
        // a usable ≥2-column slot even on a narrow terminal. Then each cell gets an equal share of
        // the width minus the (columns-1) single-space gutters between cells.
        int columns = Math.Clamp(grid.Columns <= 0 ? 1 : grid.Columns, 1, Math.Max(1, width / 2));
        int cellWidth = Math.Max(1, (width - (columns - 1)) / columns); // one space gutter between cells

        List<CardLine> output = [];
        // Walk the items in groups of `columns`. Each group emits TWO aligned rows — keys (dim) on
        // top, values (bold) below — mirroring Cauldron's grid cell (small label over big value).
        for (int start = 0; start < grid.Items.Count; start += columns)
        {
            List<CardSeg> keys = [];
            List<CardSeg> values = [];
            for (int c = 0; c < columns && start + c < grid.Items.Count; c++)
            {
                GridItem item = grid.Items[start + c];
                if (c > 0) // insert the gutter space before every cell except the first
                {
                    keys.Add(new CardSeg(" ", ""));
                    values.Add(new CardSeg(" ", ""));
                }
                // Trunc-then-Pad fixes each cell to exactly cellWidth, so the two rows line up
                // column-for-column regardless of the underlying text lengths.
                keys.Add(new CardSeg(Pad(Trunc(Plain(item.Key), cellWidth), cellWidth), MutedForeground));
                values.Add(new CardSeg(Pad(Trunc(Plain(item.Value), cellWidth), cellWidth), $"{ValueForeground} bold"));
            }
            output.Add(Content(keys));
            output.Add(Content(values));
        }
        return output;
    }

    private List<CardLine> BuildBadge(BadgeComponent badge, int width)
    {
        // Semantic variant → a "foreground on background" Spectre style. The background fill is
        // what makes it read as a pill/chip; the fg is chosen for contrast against each bg.
        // Neutral falls back to the Morgana-purple family (mediumpurple3) like Cauldron's default.
        string style = badge.Variant switch
        {
            BadgeVariant.Success => "black on green",
            BadgeVariant.Warning => "black on orange1",
            BadgeVariant.Error => "white on red",
            BadgeVariant.Info => "black on deepskyblue1",
            _ => "white on mediumpurple3"
        };
        // Upper-case + one space of breathing room each side (the "chip" padding), Cauldron-style.
        // width-2 leaves room for those two spaces so the whole chip still fits the content width.
        string chip = $" {Trunc(Plain(badge.Text).ToUpperInvariant(), Math.Max(1, width - 2))} ";
        return [Content([new CardSeg(chip, style)])];
    }

    private List<CardLine> BuildImage(ImageComponent image, int width)
    {
        // No bitmap rendering and no OSC 8 hyperlinks (the frozen low-effort decision): surface the
        // image as honest text — an "[image: alt]" tag, the URL, and an optional italic caption.
        // All dim, since it's metadata about content the terminal can't actually show. The URL is
        // WRAPPED, not truncated: a clipped URL is useless (can't be read or copied), so we'd
        // rather spend a couple of rows and keep it whole.
        string alt = string.IsNullOrWhiteSpace(image.Alt) ? "image" : Plain(image.Alt);
        List<CardLine> output = [.. WrapText($"[image: {alt}]", width).Select(slice => Content([new CardSeg(slice, MutedForeground)]))];
        if (!string.IsNullOrWhiteSpace(image.Src))
            output.AddRange(WrapText($"({image.Src})", width).Select(slice => Content([new CardSeg(slice, MutedForeground)])));
        if (!string.IsNullOrWhiteSpace(image.Caption))
            foreach (string slice in WrapText(Plain(image.Caption), width))
                output.Add(Content([new CardSeg(slice, $"{MutedForeground} italic")]));
        return output;
    }

    // ---- framing ------------------------------------------------------------------------

    /// <summary>Emits a full-width border row (top, bottom, or header separator) in the speaker colour.</summary>
    private static Markup Border(string fill, string baseColor)
        => new($"[{baseColor}]{fill}[/]");

    /// <summary>
    /// Frames one logical card line between the side borders, padding the content to
    /// <paramref name="innerWidth"/> visible columns. A divider line fills the inner span with a
    /// dim rule; a blank line emits an empty padded interior.
    /// </summary>
    private static Markup FrameLine(CardLine line, int innerWidth, string baseColor)
    {
        // Left edge: "│" in the speaker colour, then the one mandatory padding space. The 32-char
        // slack on the builder is headroom for the style tags, which add bytes but no columns.
        StringBuilder sb = new(innerWidth + 32);
        sb.Append('[').Append(baseColor).Append("]│[/] ");

        if (line.IsDivider)
        {
            // A divider ignores any segments and simply fills the whole interior with a dim rule.
            sb.Append('[').Append(DividerStyle).Append(']').Append(new string('─', innerWidth)).Append("[/]");
        }
        else
        {
            // Emit each segment, wrapping it in its [style]…[/] tag when it has one. Crucially we
            // track `visible` from the segment's terminal CELL WIDTH — not its char length and not
            // the bytes written. The markup tags and the doubling from Markup.Escape (which turns
            // "[" into "[[" so a literal bracket survives the parser) cost zero screen columns; and
            // a char count would be wrong for wide glyphs (CJK), zero-width combining marks and
            // variation selectors (e.g. ⚠️ = ⚠ + U+FE0F: two chars, one column). Measuring cells is
            // what keeps the right border aligned for those rows. Builders upstream have already
            // wrapped/truncated each line to ≤ innerWidth cells, so this never overflows.
            int visible = 0;
            foreach (CardSeg seg in line.Segs)
            {
                if (seg.Text.Length == 0)
                    continue;
                if (seg.Style.Length > 0)
                    sb.Append('[').Append(seg.Style).Append(']').Append(Markup.Escape(seg.Text)).Append("[/]");
                else
                    sb.Append(Markup.Escape(seg.Text));
                visible += seg.Text.GetCellWidth();
            }
            // Pad the remainder so the interior is always exactly innerWidth columns — this is what
            // makes the right border line up vertically no matter how short the content is.
            if (visible < innerWidth)
                sb.Append(new string(' ', innerWidth - visible));
        }

        // Right edge: the closing padding space and "│". Interior is now innerWidth, so the full
        // row is │ + space + innerWidth + space + │ = cardWidth — one terminal row, exactly.
        sb.Append(' ').Append('[').Append(baseColor).Append("]│[/]");
        return new Markup(sb.ToString());
    }

    // ---- helpers ------------------------------------------------------------------------

    /// <summary>A content card line carrying styled segments.</summary>
    private static CardLine Content(List<CardSeg> segs)
        => new CardLine(segs, IsDivider: false);

    /// <summary>An empty card line — one blank interior row.</summary>
    private static CardLine Blank()
        => new CardLine([], IsDivider: false);

    /// <summary>Strips inline markdown to plain text (a terminal cannot render it as HTML the way Cauldron does), resolves
    /// emoji shortcodes (<c>:white_check_mark:</c> → ✅) to real glyphs, then normalises emoji presentation so
    /// measured width matches what the terminal draws. Resolving shortcodes here — the single chokepoint every
    /// card builder funnels through — is what keeps them out of the rendered output: <see cref="Markup"/> does
    /// not expand them in this path, and the badge builder upper-cases its text (turning <c>:tada:</c> into the
    /// non-shortcode <c>:TADA:</c>), so the conversion must happen before either step. A resolved glyph is then
    /// measured correctly by <c>GetCellWidth</c>, keeping the right border aligned.</summary>
    private string Plain(string? text) =>
        string.IsNullOrEmpty(text) ? string.Empty : cells.StripVariationSelectors(Emoji.Replace(Markdown.ToPlainText(text, Pipeline).Trim()));

    /// <summary>Wraps to terminal columns — delegates to <see cref="TerminalCellService.Wrap"/>.</summary>
    private List<string> WrapText(string text, int width) => cells.Wrap(text, width);

    /// <summary>Truncates to terminal columns — delegates to <see cref="TerminalCellService.Trunc"/>.</summary>
    private string Trunc(string text, int width) => cells.Trunc(text, width);

    /// <summary>Right-pads with spaces to exactly <paramref name="width"/> terminal columns (cell-measured; input assumed already ≤ width).</summary>
    private static string Pad(string text, int width)
    {
        int cells = text.GetCellWidth();
        return cells >= width ? text : text + new string(' ', width - cells);
    }

    /// <summary>One styled run of visible text inside a card line. <paramref name="Style"/> is a full Spectre style token string (e.g. <c>"grey85 bold"</c>) or empty for unstyled padding.</summary>
    private readonly record struct CardSeg(string Text, string Style);

    /// <summary>One logical card line: either styled <paramref name="Segs"/> or, when <paramref name="IsDivider"/> is set, an inner dim rule filled to the content width.</summary>
    private sealed record CardLine(List<CardSeg> Segs, bool IsDivider);
}