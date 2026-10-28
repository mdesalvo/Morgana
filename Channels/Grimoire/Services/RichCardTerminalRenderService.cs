using System.Text;
using Morgana.Contracts;
using Markdig;
using Spectre.Console;
using Morgana.Terminal.Services;

namespace Grimoire.Services;

/// <summary>
/// Renders a rich card to single-row markups forming a hand-drawn bordered box, rather than variable-height renderables.
/// Card text is flattened to plain text, since a terminal has no inline markdown the way Cauldron's browser has.
/// </summary>
public sealed class RichCardTerminalRenderService
{
    /// <summary>The default Markdig pipeline, configured as Cauldron's, which flattens card text to plain text.</summary>
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().Build();

    /// <summary>Body prose foreground: Cauldron's <c>#d0d0d0</c> neutral, distinct from the speaker-tinted chat prose.</summary>
    private const string BodyForeground = "grey85";

    /// <summary>De-emphasised foreground for subtitles, keys, muted and small text and captions, as Cauldron's 0.7 opacity greys.</summary>
    private const string MutedForeground = "grey70";

    /// <summary>Foreground for values and grid cell values: Cauldron's brightest body white <c>#e0e0e0</c>.</summary>
    private const string ValueForeground = "grey93";

    /// <summary>Style of the inner divider fill line.</summary>
    private const string DividerStyle = "grey50";

    /// <summary>Wraps and truncates by terminal cell, so a wide glyph never skews the box.</summary>
    private readonly TerminalCellService cells;

    /// <summary>Captures the cell measurement the box is built against.</summary>
    public RichCardTerminalRenderService(TerminalCellService cells)
    {
        this.cells = cells;
    }

    /// <summary>
    /// Renders <paramref name="richCard"/> to single-row markups forming a bordered box up to <paramref name="width"/> cells wide.
    /// The chrome is tinted with the speaker's <paramref name="baseColor"/> so the card visibly belongs to whoever sent it
    /// while the body keeps Cauldron's neutral greys for legibility.
    /// </summary>
    public List<Markup> RenderRichCard(RichCard richCard, string baseColor, int width)
    {
        // The box owns the full width and each content row is a border, a space, the content, a space and a border,
        // so the text has the width minus 4. Everything is built and truncated to that; the framing pads it back,
        // which keeps every row exactly one terminal row, the invariant TerminalUiService.BuildBody budgets against.
        // The floor of 8 stops a pathologically narrow terminal from giving a negative inner width
        int cardWidth = Math.Max(8, width);
        int innerWidth = cardWidth - 4;

        // Phase 1 builds the logical lines, independent of the border. The header is the title, bold and in the
        // speaker's colour, plus an optional subtitle, each already cut to the inner width so phase 2 never measures again
        List<CardLine> header =
        [
            Content([new CardSeg(Trunc(Plain(richCard.Title), innerWidth), $"{baseColor} bold")])
        ];

        // The subtitle is optional and dim
        if (!string.IsNullOrWhiteSpace(richCard.Subtitle))
        {
            foreach (string slice in WrapText(Plain(richCard.Subtitle), innerWidth))
                header.Add(Content([new CardSeg(slice, MutedForeground)]));
        }

        // The body is the component tree flattened to logical lines, sections included
        List<CardLine> body = BuildComponents(richCard.Components, baseColor, innerWidth);

        // Phase 2 frames the logical lines into rows: borders are whole-width fills and content rows go through
        // FrameLine. The separator and the body are drawn only when there is a body, so a title-only card collapses to a tidy box
        List<Markup> rows =
        [
            Border($"╭{new string('─', cardWidth - 2)}╮", baseColor)
        ];
        foreach (CardLine line in header)
            rows.Add(FrameLine(line, innerWidth, baseColor));

        // The separator and the body follow the header only when the card has components
        if (body.Count > 0)
        {
            rows.Add(Border($"├{new string('─', cardWidth - 2)}┤", baseColor));
            foreach (CardLine line in body)
                rows.Add(FrameLine(line, innerWidth, baseColor));
        }

        // The bottom border closes the box
        rows.Add(Border($"╰{new string('─', cardWidth - 2)}╯", baseColor));
        return rows;
    }

    // ---- component walking --------------------------------------------------------------

    /// <summary>Walks a component list into logical card lines with a blank row between consecutive components and none leading or trailing, mirroring Cauldron's margins.</summary>
    private List<CardLine> BuildComponents(IEnumerable<CardComponent> cardComponents, string baseColor, int width)
    {
        List<CardLine> output = [];
        bool isFirstComponent = true;

        // Each component is rendered on its own and joined to the ones before it
        foreach (CardComponent cardComponent in cardComponents)
        {
            // The component's own logical lines, empty when it is of an unknown kind
            List<CardLine> componentLines = BuildComponent(cardComponent, baseColor, width);

            // An unknown or empty component contributes nothing and must not trigger a blank row, which would stand
            // with no content beside it: the first-component latch flips only once something real is emitted
            if (componentLines.Count == 0)
                continue;

            // A blank row separates a component from the one before it
            if (!isFirstComponent)
                output.Add(Blank());
            output.AddRange(componentLines);
            isFirstComponent = false;
        }

        // The components as logical lines
        return output;
    }

    /// <summary>Builds the logical lines of one component by its kind.</summary>
    private List<CardLine> BuildComponent(CardComponent component, string baseColor, int width) => component switch
    {
        // Prose, bold or dimmed
        TextBlockComponent textBlock => BuildTextBlock(textBlock, width),

        // A key and its value on one row or stacked
        KeyValueComponent keyValue => BuildKeyValue(keyValue, width),

        // A rule across the interior, which FrameLine draws
        DividerComponent => [new CardLine([], IsDivider: true)],

        // Bulleted, numbered or plain items with hanging indents
        ListComponent list => BuildList(list, baseColor, width),

        // A titled group whose children are indented
        SectionComponent section => BuildSection(section, baseColor, width),

        // Keys over values in aligned columns
        GridComponent grid => BuildGrid(grid, width),

        // A chip whose background carries its meaning
        BadgeComponent badge => BuildBadge(badge, width),

        // Text standing for an image the terminal cannot show
        ImageComponent image => BuildImage(image, width),

        // A component of a kind this renderer does not know contributes nothing
        _ => []
    };

    /// <summary>Builds a text block as one logical line per wrapped row, in the style its kind maps to.</summary>
    private List<CardLine> BuildTextBlock(TextBlockComponent textBlock, int width)
    {
        // The declared styles are mapped onto what a TTY can express: bold is real, while muted and small both
        // become the dim grey since a terminal cannot shrink a glyph
        string style = textBlock.Style switch
        {
            TextStyle.Bold => $"{BodyForeground} bold",
            TextStyle.Muted or TextStyle.Small => MutedForeground,
            _ => BodyForeground
        };
        // The plain text is wrapped to the content width and each slice becomes a line of its own
        return [.. WrapText(Plain(textBlock.Content), width).Select(slice => Content([new CardSeg(slice, style)]))];
    }

    /// <summary>Builds a key and its value on one row with the value flush right, or stacked when they do not fit together.</summary>
    private List<CardLine> BuildKeyValue(KeyValueComponent keyValue, int width)
    {
        string key = Plain(keyValue.Key);
        string value = Plain(keyValue.Value);
        string keyStyle = keyValue.Emphasize ? $"{MutedForeground} bold" : MutedForeground;
        string valueStyle = keyValue.Emphasize ? $"{ValueForeground} bold" : ValueForeground;

        // One row holds the key, at least one space and the value. Widths are cells, not chars, so wide and
        // zero-width glyphs do not skew the fill; the filler pushes the value to the right edge
        int keyCells = key.GetCellWidth();
        int valueCells = value.GetCellWidth();
        if (keyCells + valueCells + 1 <= width)
        {
            // The branch reserved one column, so the fill is at least 1 and the value never abuts the key
            int fill = width - keyCells - valueCells;
            return [Content([new CardSeg(key, keyStyle), new CardSeg(new string(' ', fill), ""), new CardSeg(value, valueStyle)])];
        }

        // Otherwise the key stacks over the value, wrapped and left-aligned. Wrapping rather than truncating keeps a
        // long prose value whole; the two stay apart by colour alone, so no indent is needed
        List<CardLine> lines = [.. WrapText(key, width).Select(slice => Content([new CardSeg(slice, keyStyle)]))];
        lines.AddRange(WrapText(value, width).Select(slice => Content([new CardSeg(slice, valueStyle)])));
        return lines;
    }

    /// <summary>Builds a list whose items hang under their own text, not under the marker.</summary>
    private List<CardLine> BuildList(ListComponent list, string baseColor, int width)
    {
        List<CardLine> output = [];

        // Only the numbered style shows the ordinal, but it advances with every item to stay in step
        int ordinal = 1;
        foreach (string rawItem in list.Items)
        {
            // The marker is chrome in the speaker's colour; the glyph is Cauldron's bullet
            string marker = list.Style switch
            {
                ListStyle.Numbered => $"{ordinal}. ",
                ListStyle.Plain => string.Empty,
                _ => "✦ "
            };

            // The next item of a numbered list counts one more
            ordinal++;
            string padding = new(' ', marker.Length);

            // The text is wrapped in the width left of the marker: the first row gets the marker and the others
            // blanks of the same width, which is the hanging indent
            int textWidth = Math.Max(1, width - marker.Length);
            List<string> itemRows = WrapText(Plain(rawItem), textWidth);

            // Each wrapped row of the item becomes a line of its own
            for (int index = 0; index < itemRows.Count; index++)
            {
                // The marker leads the first row and the blanks of the same width lead the others
                CardSeg prefix = new(index == 0 ? marker : padding, baseColor);
                output.Add(Content([prefix, new CardSeg(itemRows[index], BodyForeground)]));
            }
        }

        // The items as logical lines
        return output;
    }

    /// <summary>Builds a titled section whose children are indented two columns, nested sections indenting further.</summary>
    private List<CardLine> BuildSection(SectionComponent section, string baseColor, int width)
    {
        // No leading blank row here: BuildComponents already puts one before every component but the first
        List<CardLine> output = [];
        foreach (string slice in WrapText(Plain(section.Title), width))
            output.Add(Content([new CardSeg(slice, $"{baseColor} bold")]));

        // The subtitle is optional and dim
        if (!string.IsNullOrWhiteSpace(section.Subtitle))
            foreach (string slice in WrapText(Plain(section.Subtitle), width))
                output.Add(Content([new CardSeg(slice, MutedForeground)]));

        // Children are built at the reduced width, so the indent never pushes a line past the content edge
        const string indent = "  ";
        int childWidth = Math.Max(1, width - indent.Length);
        foreach (CardLine child in BuildComponents(section.Components, baseColor, childWidth))
        {
            // A divider has no segments, its rule being drawn by FrameLine at the full inner width, so it passes untouched
            if (child.IsDivider)
            {
                output.Add(child);
                continue;
            }

            // Every other line gains a leading indent, which composes for nested sections
            List<CardSeg> indentedSegments = [new CardSeg(indent, ""), .. child.Segs];
            output.Add(new CardLine(indentedSegments, IsDivider: false));
        }

        // The title, the subtitle and the indented children
        return output;
    }

    /// <summary>Builds a grid as pairs of aligned rows, keys dim over values bold, as Cauldron's small label over big value.</summary>
    private List<CardLine> BuildGrid(GridComponent grid, int width)
    {
        // The column count is at least 1 and at most half the width, so every cell keeps a usable slot on a narrow
        // terminal. Each cell then gets an equal share of the width minus the one-space gutters between cells
        int columns = Math.Clamp(grid.Columns <= 0 ? 1 : grid.Columns, 1, Math.Max(1, width / 2));
        int cellWidth = Math.Max(1, (width - (columns - 1)) / columns);


        // The pairs of rows of the grid, in order
        List<CardLine> output = [];

        // The items are taken in groups of one row of columns
        for (int start = 0; start < grid.Items.Count; start += columns)
        {
            List<CardSeg> keys = [];
            List<CardSeg> values = [];

            // Each cell of the group adds its key and its value to the two rows
            for (int column = 0; column < columns && start + column < grid.Items.Count; column++)
            {
                // The item that fills this cell
                GridItem item = grid.Items[start + column];

                // A gutter space separates every cell from the one before it
                if (column > 0)
                {
                    keys.Add(new CardSeg(" ", ""));
                    values.Add(new CardSeg(" ", ""));
                }

                // Truncating then padding fixes each cell to the cell width, so the two rows line up whatever the text lengths
                keys.Add(new CardSeg(Pad(Trunc(Plain(item.Key), cellWidth), cellWidth), MutedForeground));
                values.Add(new CardSeg(Pad(Trunc(Plain(item.Value), cellWidth), cellWidth), $"{ValueForeground} bold"));
            }

            // The keys row sits over the values row
            output.Add(Content(keys));
            output.Add(Content(values));
        }

        // The pairs of rows
        return output;
    }

    /// <summary>Builds a badge as a chip: upper-case text on a background that carries the variant's meaning.</summary>
    private List<CardLine> BuildBadge(BadgeComponent badge, int width)
    {
        // The variant becomes a foreground on a background, the fill making it read as a chip and the foreground
        // chosen for contrast. Neutral falls back to the purple family, like Cauldron's default
        string style = badge.Variant switch
        {
            BadgeVariant.Success => "black on green",
            BadgeVariant.Warning => "black on orange1",
            BadgeVariant.Error => "white on red",
            BadgeVariant.Info => "black on deepskyblue1",
            _ => "white on mediumpurple3"
        };
        // The text is upper-case with a space of padding each side, Cauldron-style; two columns are kept for those spaces so the chip fits
        string chip = $" {Trunc(Plain(badge.Text).ToUpperInvariant(), Math.Max(1, width - 2))} ";
        return [Content([new CardSeg(chip, style)])];
    }

    /// <summary>Builds an image as honest dim text: an alt tag, the address and an optional italic caption.</summary>
    private List<CardLine> BuildImage(ImageComponent image, int width)
    {
        // The terminal shows no bitmap and no hyperlink, so the image stands for itself in text, dim because it is
        // metadata about content that cannot be shown. The address is wrapped, not truncated: a clipped one cannot be read or copied
        string alt = string.IsNullOrWhiteSpace(image.Alt) ? "image" : Plain(image.Alt);
        List<CardLine> output = [.. WrapText($"[image: {alt}]", width).Select(slice => Content([new CardSeg(slice, MutedForeground)]))];

        // The address follows the tag when the image has one
        if (!string.IsNullOrWhiteSpace(image.Src))
            output.AddRange(WrapText($"({TerminalCellService.StripControlCharacters(image.Src)})", width).Select(slice => Content([new CardSeg(slice, MutedForeground)])));

        // The caption closes the block when the image has one
        if (!string.IsNullOrWhiteSpace(image.Caption))
            foreach (string slice in WrapText(Plain(image.Caption), width))
                output.Add(Content([new CardSeg(slice, $"{MutedForeground} italic")]));
        return output;
    }

    // ---- framing ------------------------------------------------------------------------

    /// <summary>Draws a full-width border row (top, bottom or header separator) in the speaker's colour.</summary>
    private static Markup Border(string fill, string baseColor)
        => new($"[{baseColor}]{fill}[/]");

    /// <summary>
    /// Frames one logical card line between the side borders, padding the content to <paramref name="innerWidth"/> columns.
    /// A divider fills the interior with a dim rule and a blank line gives an empty padded interior.
    /// </summary>
    private static Markup FrameLine(CardLine line, int innerWidth, string baseColor)
    {
        // The left edge is the border in the speaker's colour and the mandatory padding space. The slack on the
        // builder is headroom for the style tags, which add bytes but no columns
        StringBuilder rowMarkup = new(innerWidth + 32);
        rowMarkup.Append('[').Append(baseColor).Append("]│[/] ");

        // A divider ignores any segments and fills the whole interior with a dim rule
        if (line.IsDivider)
        {
            rowMarkup.Append('[').Append(DividerStyle).Append(']').Append(new string('─', innerWidth)).Append("[/]");
        }
        else
        {
            // The visible width is tracked from each segment's cell width, never from its chars or the bytes written.
            // Markup tags and the doubling of Markup.Escape cost no columns, while a char count would be wrong for wide
            // glyphs and for zero-width marks such as the selector of a warning sign (two chars, one column)
            int visibleCells = 0;
            foreach (CardSeg segment in line.Segs)
            {
                // An empty segment draws nothing
                if (segment.Text.Length == 0)
                    continue;

                // A styled segment is wrapped in its tag and an unstyled one is only escaped
                if (segment.Style.Length > 0)
                    rowMarkup.Append('[').Append(segment.Style).Append(']').Append(Markup.Escape(segment.Text)).Append("[/]");
                else
                    rowMarkup.Append(Markup.Escape(segment.Text));
                visibleCells += segment.Text.GetCellWidth();
            }

            // The remainder is padded, so the interior is always the inner width and the right border lines up whatever the content
            if (visibleCells < innerWidth)
                rowMarkup.Append(new string(' ', innerWidth - visibleCells));
        }

        // The right edge is the closing space and the border, which makes the row exactly the card width
        rowMarkup.Append(' ').Append('[').Append(baseColor).Append("]│[/]");
        return new Markup(rowMarkup.ToString());
    }

    // ---- helpers ------------------------------------------------------------------------

    /// <summary>A content card line carrying styled segments.</summary>
    private static CardLine Content(List<CardSeg> segments)
        => new CardLine(segments, IsDivider: false);

    /// <summary>An empty card line, one blank interior row.</summary>
    private static CardLine Blank()
        => new CardLine([], IsDivider: false);

    /// <summary>
    /// Flattens text to plain terminal text: control characters out, markdown stripped, emoji shortcodes resolved to glyphs and
    /// their presentation normalised so the width measured is the width drawn. Every card builder funnels through here, which
    /// is why shortcodes are resolved here: Markup does not expand them and the badge upper-cases its text, turning one into a non-shortcode.
    /// </summary>
    private string Plain(string? text) =>
        string.IsNullOrEmpty(text)
            ? string.Empty
            : cells.StripVariationSelectors(Emoji.Replace(
                Markdown.ToPlainText(TerminalCellService.StripControlCharacters(text), Pipeline).Trim()));

    /// <summary>Wraps to terminal columns through <see cref="TerminalCellService.Wrap"/>.</summary>
    private List<string> WrapText(string text, int width) => cells.Wrap(text, width);

    /// <summary>Truncates to terminal columns through <see cref="TerminalCellService.Trunc"/>.</summary>
    private string Trunc(string text, int width) => cells.Trunc(text, width);

    /// <summary>Right-pads with spaces to exactly <paramref name="width"/> terminal columns, the text being already at most that wide.</summary>
    private static string Pad(string text, int width)
    {
        // Measured in cells, as everything else on a row
        int textCells = text.GetCellWidth();
        return textCells >= width ? text : text + new string(' ', width - textCells);
    }

    /// <summary>One styled run of visible text inside a card line.</summary>
    /// <param name="Text">The visible text.</param>
    /// <param name="Style">A full Spectre style string such as <c>grey85 bold</c>, or empty for unstyled padding.</param>
    private readonly record struct CardSeg(string Text, string Style);

    /// <summary>One logical card line: styled segments or, for a divider, a dim rule filled to the content width.</summary>
    /// <param name="Segs">The styled runs of the line, in order.</param>
    /// <param name="IsDivider">True for a divider, which has no segments.</param>
    private sealed record CardLine(List<CardSeg> Segs, bool IsDivider);
}