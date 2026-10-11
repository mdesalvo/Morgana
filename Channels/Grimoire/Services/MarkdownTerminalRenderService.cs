using System.Globalization;
using System.Text;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Spectre.Console;
using Morgana.Terminal.Services;

namespace Grimoire.Services;

/// <summary>
/// Renders markdown to terminal rows, one markup per row. Markdig parses the source into a tree, <c>Render</c> walks it
/// into logical lines of styled spans and <c>Wrap</c> breaks them at the terminal width by cell. Block widgets
/// such as panels and tables are left to the rich card renderer.
/// </summary>
public sealed class MarkdownTerminalRenderService
{
    /// <summary>The default Markdig pipeline, the same configuration Cauldron's renderer uses.</summary>
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().Build();

    /// <summary>Foreground for fenced and indented code: a neutral light grey, so code reads as other against the speaker-coloured prose.</summary>
    private const string CodeBlockForeground = "grey85";

    /// <summary>Subtle dark background shared by inline code and code blocks, marking the monospace region without stealing the foreground colour.</summary>
    private const string CodeBackground = "on grey19";

    /// <summary>Foreground for de-emphasised chrome: blockquote bars and the address that trails a link.</summary>
    private const string ChromeForeground = "grey";

    /// <summary>Style of the horizontal-rule fill line.</summary>
    private const string RuleStyle = "grey50";

    /// <summary>Wraps by terminal cell, so a wide glyph is never split nor measured as one column.</summary>
    private readonly TerminalCellService cells;

    /// <summary>Captures the cell measurement the rows are wrapped against.</summary>
    public MarkdownTerminalRenderService(TerminalCellService cells)
    {
        this.cells = cells;
    }

    /// <summary>One styled run of visible text.</summary>
    /// <param name="Text">The visible text, cleaned of control characters.</param>
    /// <param name="Foreground">A concrete Spectre colour token: the speaker's colour unless the construct overrides it, as code does.</param>
    /// <param name="Decorations">The style tokens added after the colour (<c>bold</c>, <c>italic</c>, <c>underline</c>, a background) or an empty string.</param>
    internal readonly record struct StyledSpan(string Text, string Foreground, string Decorations);

    /// <summary>One logical line, which wraps into one or more rows, or a horizontal rule across the whole row.</summary>
    /// <param name="Spans">The styled runs of the line, in order.</param>
    /// <param name="IsRule">True for a rule, which has no spans.</param>
    internal sealed record RenderedLine(List<StyledSpan> Spans, bool IsRule);

    /// <summary>
    /// Renders <paramref name="markdown"/> in the speaker's <paramref name="baseColor"/> to rows of at most <paramref name="width"/> cells,
    /// with a bold <paramref name="speakerPrefix"/> on the first row unless it is null. A <paramref name="speakerMarkerColor"/> puts a dot
    /// in that colour ahead of the prefix, drawn only: it is no part of the message.
    /// </summary>
    public List<Markup> RenderToRows(string markdown, string baseColor, string? speakerPrefix, int width, string? speakerMarkerColor = null)
    {
        List<RenderedLine> lines = Render(markdown, baseColor);

        // A streamed partial reply has no prefix: the speaker is named once the reply is committed
        if (speakerPrefix is { Length: > 0 })
            PrependSpeaker(lines, speakerPrefix, baseColor, speakerMarkerColor);

        // The logical lines wrapped to the width
        return Wrap(lines, width);
    }

    /// <summary>Walks the Markdig tree of <paramref name="markdown"/> into logical lines, colouring prose in <paramref name="baseColor"/>.</summary>
    internal List<RenderedLine> Render(string markdown, string baseColor)
    {
        // Emoji shortcodes are resolved to glyphs up front, as the rich card does: Markup does not expand them, so a
        // model that emits GitHub-style shortcodes would otherwise leave them literal. The variation selectors
        // are stripped afterwards so Wrap measures what the terminal draws for an emoji-presentation sequence
        MarkdownDocument document = Markdown.Parse(cells.StripVariationSelectors(Emoji.Replace(markdown ?? string.Empty)), Pipeline);
        List<RenderedLine> lines = RenderBlocks(document, baseColor);

        // An empty document still owes one line, so the speaker prefix has somewhere to land and the row does not vanish
        if (lines.Count == 0)
            lines.Add(new RenderedLine([], false));
        return lines;
    }

    /// <summary>
    /// Prepends a bold speaker tag, behind a dot in <paramref name="markerColor"/> when given, to the first
    /// renderable (non-rule) line. A message with none gets a new leading line to carry it.
    /// </summary>
    internal static void PrependSpeaker(List<RenderedLine> lines, string speakerPrefix, string baseColor, string? markerColor = null)
    {
        // The prefix is the speaker's name in bold, cleaned like any text from the network
        StyledSpan prefix = new(TerminalCellService.StripControlCharacters(speakerPrefix), baseColor, "bold");

        // The prefix goes on the first line that is not a rule
        int target = lines.FindIndex(line => !line.IsRule);
        if (target < 0)
        {
            // A message of rules only gets a new first line to carry the speaker
            target = 0;
            lines.Insert(0, new RenderedLine([], false));
        }
        // The speaker leads the line
        lines[target].Spans.Insert(0, prefix);

        // The dot marks where a speaker starts in a long transcript, in the speaker's own colour even when
        // the row wears a warning's: who spoke and what kind of line it is read apart
        if (markerColor is not null)
            lines[target].Spans.Insert(0, new StyledSpan("● ", markerColor, string.Empty));
    }

    // ---- block walking ----------------------------------------------------------------

    /// <summary>Renders the blocks of a container one after the other, one blank line between consecutive top-level blocks and none leading or trailing.</summary>
    private static List<RenderedLine> RenderBlocks(IEnumerable<Block> blocks, string baseColor)
    {
        List<RenderedLine> output = [];
        bool isFirstBlock = true;
        foreach (Block block in blocks)
        {
            // The block renders to the logical lines it stands for
            List<RenderedLine> blockLines = RenderBlock(block, baseColor);

            // A block that renders nothing, such as raw HTML, leaves no trace and no separator
            if (blockLines.Count == 0)
                continue;

            // The blank row mirrors the vertical rhythm Cauldron gets from paragraph margins
            if (!isFirstBlock)
                output.Add(new RenderedLine([], false));
            output.AddRange(blockLines);
            isFirstBlock = false;
        }

        // The blocks as logical lines
        return output;
    }

    /// <summary>Renders one block to logical lines according to its kind.</summary>
    private static List<RenderedLine> RenderBlock(Block block, string baseColor) => block switch
    {
        // A heading is a paragraph in bold
        HeadingBlock heading => RenderInlines(heading.Inline, baseColor, "bold"),

        // A paragraph is its inlines in the speaker's colour
        ParagraphBlock paragraph => RenderInlines(paragraph.Inline, baseColor, ""),

        // A list gets a bullet or an ordinal per item
        ListBlock list => RenderList(list, baseColor),

        // A quote gets a bar before every row
        QuoteBlock quote => RenderQuote(quote, baseColor),

        // A thematic break is a rule across the whole width
        ThematicBreakBlock => [new RenderedLine([], true)],

        // Code keeps its lines as they are on a background of its own
        CodeBlock code => RenderCodeBlock(code),

        // Other containers such as nested wrappers still have their children rendered
        ContainerBlock container => RenderBlocks(container, baseColor),

        // Anything else, such as an HTML block, means nothing in a TTY and is dropped
        _ => []
    };

    /// <summary>Renders a list: the first line of each item carries the bullet or the ordinal and the continuation rows align under it.</summary>
    private static List<RenderedLine> RenderList(ListBlock list, string baseColor)
    {
        // The lines of every item, in order
        List<RenderedLine> output = [];

        // An ordered list counts from the number it declares, or from 1 when it declares none
        int ordinal = list.IsOrdered && int.TryParse(list.OrderedStart, NumberStyles.Integer, CultureInfo.InvariantCulture, out int start) ? start : 1;

        // Each item of the list is a block of its own
        foreach (Block item in list)
        {
            // The marker and the blanks of the same width that align the continuation rows
            string bullet = list.IsOrdered ? $"{ordinal}. " : "• ";
            string padding = new(' ', bullet.Length);
            ordinal++;

            // An item holds blocks of its own, which are rendered like any others
            List<RenderedLine> itemLines = item is ListItemBlock listItem
                ? RenderBlocks(listItem, baseColor)
                : RenderBlock(item, baseColor);

            // Each line of the item is marked in turn
            for (int index = 0; index < itemLines.Count; index++)
            {
                // The line about to be marked
                RenderedLine line = itemLines[index];

                // A rule inside an item spans the row and takes no marker
                if (line.IsRule)
                {
                    // The rule is kept as it is
                    output.Add(line);
                    continue;
                }

                // The first line carries the marker and the others align under it
                line.Spans.Insert(0, new StyledSpan(index == 0 ? bullet : padding, baseColor, ""));
                output.Add(line);
            }
        }

        // The items as logical lines
        return output;
    }

    /// <summary>Renders a quote: its blocks with a dim bar before every row that is not a rule.</summary>
    private static List<RenderedLine> RenderQuote(QuoteBlock quote, string baseColor)
    {
        List<RenderedLine> quotedLines = RenderBlocks(quote, baseColor);
        foreach (RenderedLine line in quotedLines)
        {
            // A rule is drawn across the row and takes no bar
            if (line.IsRule)
                continue;
            line.Spans.Insert(0, new StyledSpan("│ ", ChromeForeground, ""));
        }

        // The quoted blocks as logical lines
        return quotedLines;
    }

    /// <summary>Renders a code block: one logical line per source line, in the code colours.</summary>
    private static List<RenderedLine> RenderCodeBlock(CodeBlock code)
    {
        List<RenderedLine> output = [];
        for (int index = 0; index < code.Lines.Count; index++)
        {
            // Code comes from the network like any text, so control characters are stripped
            string text = TerminalCellService.StripControlCharacters(code.Lines.Lines[index].Slice.ToString());
            output.Add(new RenderedLine([new StyledSpan(text, CodeBlockForeground, CodeBackground)], false));
        }

        // The source lines as logical lines
        return output;
    }

    // ---- inline walking ---------------------------------------------------------------

    /// <summary>
    /// Renders an inline container into logical lines: hard line breaks split lines and soft breaks become spaces.
    /// <paramref name="decorations"/> is the style inherited from the enclosing emphasis.
    /// </summary>
    private static List<RenderedLine> RenderInlines(ContainerInline? container, string baseColor, string decorations)
    {
        List<RenderedLine> lines = [];
        List<StyledSpan> currentSpans = [];

        // A paragraph with no inlines still yields one empty line
        if (container is not null)
            WalkInlines(container, baseColor, decorations, lines, ref currentSpans);

        // The spans after the last hard break are the last line
        lines.Add(new RenderedLine(currentSpans, false));
        return lines;
    }

    /// <summary>Walks the inlines of <paramref name="container"/> appending styled spans and closing a line at every hard break.</summary>
    private static void WalkInlines(ContainerInline container, string baseColor, string decorations,
        List<RenderedLine> lines, ref List<StyledSpan> currentSpans)
    {
        // Each inline is rendered by its kind
        foreach (Inline inline in container)
        {
            // An inline of a kind not listed here renders nothing
            switch (inline)
            {
                // Plain text in the speaker's colour
                case LiteralInline literal:
                    Append(currentSpans, literal.Content.ToString(), baseColor, decorations);
                    break;

                // One delimiter is italic, two are bold and three are both, added to whatever style is in effect so nesting composes
                case EmphasisInline emphasis:
                    bool bold = emphasis.DelimiterCount >= 2;
                    bool italic = emphasis.DelimiterCount % 2 == 1;
                    string nestedDecorations = decorations;
                    if (bold) nestedDecorations = Combine(nestedDecorations, "bold");
                    if (italic) nestedDecorations = Combine(nestedDecorations, "italic");
                    WalkInlines(emphasis, baseColor, nestedDecorations, lines, ref currentSpans);
                    break;

                // Inline code keeps the speaker's colour and is marked by the monospace background
                case CodeInline codeInline:
                    Append(currentSpans, codeInline.Content, baseColor, Combine(decorations, CodeBackground));
                    break;

                // A link or an image gets its label styled and its address shown
                case LinkInline link:
                    RenderLink(link, baseColor, decorations, lines, ref currentSpans);
                    break;

                // A bare address is underlined
                case AutolinkInline autolink:
                    Append(currentSpans, autolink.Url, baseColor, Combine(decorations, "underline"));
                    break;

                // A hard break ends the line while a soft break is a space within it
                case LineBreakInline lineBreak:
                    if (lineBreak.IsHard)
                    {
                        // The line is closed with the spans gathered so far and a new one begins
                        lines.Add(new RenderedLine(currentSpans, false));
                        currentSpans = [];
                    }
                    else
                    {
                        // A soft break reads as a space
                        Append(currentSpans, " ", baseColor, decorations);
                    }
                    break;

                // Raw HTML tags carry nothing a terminal can show
                case HtmlInline:
                    break;

                // Any other container still has its children walked
                case ContainerInline nestedContainer:
                    WalkInlines(nestedContainer, baseColor, decorations, lines, ref currentSpans);
                    break;
            }
        }
    }

    /// <summary>Renders a link's label underlined, then its address dimmed in parentheses when it adds something to the label; an image shows its alt text and source.</summary>
    private static void RenderLink(LinkInline link, string baseColor, string decorations,
        List<RenderedLine> lines, ref List<StyledSpan> currentSpans)
    {
        // The label decides whether the address adds anything
        string label = LinkLabel(link);

        // A terminal cannot show an image, so the alt text and the source stand in for it
        if (link.IsImage)
        {
            // An image without alt text is called image
            string altText = label.Length > 0 ? label : "image";
            Append(currentSpans, $"[image: {altText}]", ChromeForeground, decorations);
            if (!string.IsNullOrEmpty(link.Url))
                Append(currentSpans, $" ({link.Url})", ChromeForeground, "");
            return;
        }

        // The label is walked as inlines so emphasis inside it survives
        WalkInlines(link, baseColor, Combine(decorations, "underline"), lines, ref currentSpans);

        // The address is shown unless the label already says it
        if (!string.IsNullOrEmpty(link.Url) && !string.Equals(link.Url, label, StringComparison.Ordinal))
            Append(currentSpans, $" ({link.Url})", ChromeForeground, "");
    }

    /// <summary>Flattens a link's child inlines to their plain text, to decide whether the address repeats the label.</summary>
    private static string LinkLabel(LinkInline link)
    {
        StringBuilder label = new();
        foreach (Inline child in link)
            if (child is LiteralInline literal)
                label.Append(literal.Content.ToString());
        return label.ToString();
    }

    /// <summary>Adds a span of <paramref name="text"/> to the line, cleaned of control characters; nothing for an empty text.</summary>
    private static void Append(List<StyledSpan> spans, string text, string foreground, string decorations)
    {
        // Text from the network is cleaned before it can reach the terminal
        text = TerminalCellService.StripControlCharacters(text);
        if (text.Length > 0)
            spans.Add(new StyledSpan(text, foreground, decorations));
    }

    /// <summary>Joins two decoration tokens with a space, tolerating either being empty.</summary>
    private static string Combine(string existingDecorations, string addedDecoration) =>
        existingDecorations.Length == 0 ? addedDecoration : addedDecoration.Length == 0 ? existingDecorations : $"{existingDecorations} {addedDecoration}";

    // ---- wrapping ---------------------------------------------------------------------

    /// <summary>Breaks each logical line into single-row markups at <paramref name="width"/>, measuring by cell so a wide glyph never desyncs the row budget <c>TerminalUiService.BuildBody</c> relies on.</summary>
    internal List<Markup> Wrap(List<RenderedLine> lines, int width)
    {
        // A terminal reporting no width still gets one cell per row
        width = Math.Max(1, width);
        List<Markup> rows = [];

        // Each logical line becomes one or more rows
        foreach (RenderedLine line in lines)
        {
            // A rule fills one whole row
            if (line.IsRule)
            {
                // The rule spans the whole row
                rows.Add(new Markup($"[{RuleStyle}]{new string('─', width)}[/]"));
                continue;
            }

            // A line with no spans is a blank row, the separator between blocks
            if (line.Spans.Count == 0)
            {
                // A blank row is the separator between blocks
                rows.Add(new Markup(string.Empty));
                continue;
            }

            // The row being built, with the cells it already occupies
            StringBuilder rowMarkup = new(width + 32);
            int rowCells = 0;
            int rowsBefore = rows.Count;

            // Each span is cut into the slices of the rows it falls on
            foreach (StyledSpan span in line.Spans)
            {
                // The span is walked rune by rune, collecting a same-style slice for the current row, so a wide
                // glyph is never split and the row closes exactly when the next rune would overflow it
                StringBuilder slice = new();
                int sliceCells = 0;

                // The runes are taken in order
                foreach (Rune rune in span.Text.EnumerateRunes())
                {
                    // The rune is measured in the columns the terminal draws
                    int runeCells = cells.RuneCells(rune);

                    // The row closes before a rune that would overflow it, but never while empty, so a rune wider than the whole
                    // terminal still lands somewhere instead of looping forever. The pending slice is flushed first so its style tag closes before the row does
                    if (rowCells + sliceCells + runeCells > width && rowCells + sliceCells > 0)
                    {
                        // The slice pending is written first so its style closes before the row does
                        if (slice.Length > 0)
                        {
                            // The slice is written into the row
                            AppendSegment(rowMarkup, span, slice.ToString());
                            slice.Clear();
                            sliceCells = 0;
                        }
                        // The full row is emitted
                        rows.Add(new Markup(rowMarkup.ToString()));
                        rowMarkup.Clear();
                        rowCells = 0;
                    }

                    // The rune joins the slice of the row being built
                    slice.Append(rune.ToString());
                    sliceCells += runeCells;
                }

                // What is left of the span goes to the row being built
                if (slice.Length > 0)
                {
                    // The tail of the span goes to the row being built
                    AppendSegment(rowMarkup, span, slice.ToString());
                    rowCells += sliceCells;
                }
            }

            // The last partial row is flushed; a line whose spans were all empty still gets its row
            if (rowMarkup.Length > 0 || rows.Count == rowsBefore)
                rows.Add(new Markup(rowMarkup.ToString()));
        }

        // Rows of exactly one terminal row each
        return rows;
    }

    /// <summary>Appends one escaped segment to the row, wrapped in the span's colour and decorations.</summary>
    private static void AppendSegment(StringBuilder rowMarkup, StyledSpan span, string slice)
    {
        string tag = span.Decorations.Length > 0 ? $"{span.Foreground} {span.Decorations}" : span.Foreground;
        rowMarkup.Append('[').Append(tag).Append(']').Append(Markup.Escape(slice)).Append("[/]");
    }
}
