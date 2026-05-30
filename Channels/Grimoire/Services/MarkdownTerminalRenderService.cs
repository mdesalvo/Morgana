using System.Text;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Spectre.Console;

namespace Grimoire.Services;

/// <summary>
/// Turns Morgana's full (non-degraded) markdown into a flat stream of single-row
/// Spectre <see cref="Markup"/>s — the terminal-side counterpart of Cauldron's
/// <c>MarkdownRendererService.ToHtml</c>. Where Cauldron hands the markdown to the
/// browser's HTML+CSS engine (which lays out and wraps for free), Grimoire has no
/// layout engine: <see cref="ConsoleUiService.BuildBody"/> does its own row budgeting
/// on the strict contract "one <see cref="Markup"/> = exactly one terminal row". This
/// renderer therefore <b>flattens markdown to inline-styled lines</b> rather than to
/// arbitrary multi-row <c>IRenderable</c>s: headings, bold/italic, code, lists, links,
/// blockquotes and horizontal rules all collapse into styled text rows. The genuinely
/// block-level widgets (Panel, Table, Rule) are reserved for the rich-card mapper
/// (step 4), where measured multi-row layout is justified.
/// </summary>
/// <remarks>
/// The two-stage pipeline mirrors the model the rest of the UI already relies on:
/// <list type="number">
///   <item>Markdig parses the source into an AST; <see cref="Render"/> walks it into a
///     list of <see cref="RenderedLine"/>s, each a sequence of <see cref="StyledSpan"/>s
///     carrying <b>visible</b> text plus its Spectre style tokens (the markup tags
///     themselves occupy zero columns).</item>
///   <item><see cref="Wrap"/> breaks those logical lines at the terminal width counting
///     visible columns only, preserving span boundaries, and emits one balanced
///     <see cref="Markup"/> per visual row.</item>
/// </list>
/// Because soft line breaks collapse to spaces and blank lines separate blocks — exactly
/// as CommonMark/Markdig render to HTML — the on-screen result matches Cauldron's bubble
/// for the same payload.
/// </remarks>
public static class MarkdownTerminalRenderService
{
    /// <summary>Shared default pipeline — same configuration Cauldron's renderer uses.</summary>
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().Build();

    /// <summary>Foreground for fenced/indented code blocks: a neutral light grey so a code block reads as "other" against the speaker-coloured prose.</summary>
    private const string CodeBlockForeground = "grey85";

    /// <summary>Subtle dark background shared by inline code and code blocks, marking the monospace region without stealing the foreground colour.</summary>
    private const string CodeBackground = "on grey19";

    /// <summary>Foreground for de-emphasised chrome: blockquote bars and the trailing <c>(url)</c> after a link.</summary>
    private const string ChromeForeground = "grey";

    /// <summary>Style of the horizontal-rule fill line.</summary>
    private const string RuleStyle = "grey50";

    /// <summary>
    /// One styled run of visible text. <paramref name="Foreground"/> is always a concrete
    /// Spectre colour token (the base speaker colour, resolved at render time, unless the
    /// construct overrides it — e.g. code blocks). <paramref name="Decorations"/> holds the
    /// additive style tokens appended after the colour (<c>bold</c>, <c>italic</c>,
    /// <c>underline</c>, <c>on grey19</c>, …) or <see cref="string.Empty"/>.
    /// </summary>
    internal readonly record struct StyledSpan(string Text, string Foreground, string Decorations);

    /// <summary>
    /// One logical line: either a sequence of <paramref name="Spans"/> (wrapped at width
    /// into one-or-more visual rows) or, when <paramref name="IsRule"/> is set, a
    /// horizontal rule filled to the full terminal width as a single row.
    /// </summary>
    internal sealed record RenderedLine(List<StyledSpan> Spans, bool IsRule);

    /// <summary>
    /// Full convenience path used by the UI: parse <paramref name="markdown"/> with the
    /// speaker's <paramref name="baseColor"/> as the prose colour, optionally seed the very
    /// first row with a bold <paramref name="speakerPrefix"/> (e.g. <c>"Morgana: "</c>), and
    /// wrap the result to single-row <see cref="Markup"/>s at <paramref name="width"/>.
    /// Streaming callers pass a null prefix.
    /// </summary>
    public static List<Markup> RenderToRows(string markdown, string baseColor, string? speakerPrefix, int width)
    {
        List<RenderedLine> lines = Render(markdown, baseColor);
        if (speakerPrefix is { Length: > 0 })
            PrependSpeaker(lines, speakerPrefix, baseColor);
        return Wrap(lines, width);
    }

    /// <summary>Walks the Markdig AST of <paramref name="markdown"/> into logical lines, colouring prose in <paramref name="baseColor"/>.</summary>
    internal static List<RenderedLine> Render(string markdown, string baseColor)
    {
        // Resolve emoji shortcodes (:tada: → 🎉) to real glyphs up front, mirroring the rich-card
        // path (RichCardTerminalRenderService.Plain): Markup does not expand them downstream, so a
        // model that emits GitHub-style shortcodes in prose would otherwise leave them literal.
        MarkdownDocument document = Markdown.Parse(Emoji.Replace(markdown ?? string.Empty), Pipeline);
        List<RenderedLine> lines = RenderBlocks(document, baseColor);
        // An empty document (e.g. whitespace-only message) still owes one line so the
        // speaker prefix has somewhere to land and the row never silently vanishes.
        if (lines.Count == 0)
            lines.Add(new RenderedLine([], false));
        return lines;
    }

    /// <summary>Prepends a bold speaker tag to the first renderable (non-rule) line, or inserts a new leading line if there is none.</summary>
    internal static void PrependSpeaker(List<RenderedLine> lines, string speakerPrefix, string baseColor)
    {
        StyledSpan prefix = new(speakerPrefix, baseColor, "bold");
        int target = lines.FindIndex(l => !l.IsRule);
        if (target < 0)
        {
            lines.Insert(0, new RenderedLine([prefix], false));
            return;
        }
        lines[target].Spans.Insert(0, prefix);
    }

    // ---- block walking ----------------------------------------------------------------

    private static List<RenderedLine> RenderBlocks(IEnumerable<Block> blocks, string baseColor)
    {
        List<RenderedLine> output = [];
        bool first = true;
        foreach (Block block in blocks)
        {
            List<RenderedLine> rendered = RenderBlock(block, baseColor);
            if (rendered.Count == 0)
                continue;

            // Blank separator between consecutive top-level blocks — mirrors the vertical
            // rhythm Cauldron gets from paragraph margins. No leading/trailing blank.
            if (!first)
                output.Add(new RenderedLine([], false));
            output.AddRange(rendered);
            first = false;
        }
        return output;
    }

    private static List<RenderedLine> RenderBlock(Block block, string baseColor) => block switch
    {
        HeadingBlock heading => RenderInlines(heading.Inline, baseColor, "bold"),
        ParagraphBlock paragraph => RenderInlines(paragraph.Inline, baseColor, ""),
        ListBlock list => RenderList(list, baseColor),
        QuoteBlock quote => RenderQuote(quote, baseColor),
        ThematicBreakBlock => [new RenderedLine([], true)],
        CodeBlock code => RenderCodeBlock(code),
        // ContainerBlocks we don't special-case (e.g. nested wrappers) still get their
        // children rendered; anything else (HtmlBlock, …) is dropped — not meaningful in a TTY.
        ContainerBlock container => RenderBlocks(container, baseColor),
        _ => []
    };

    private static List<RenderedLine> RenderList(ListBlock list, string baseColor)
    {
        List<RenderedLine> output = [];
        int ordinal = list.IsOrdered && int.TryParse(list.OrderedStart, out int start) ? start : 1;

        foreach (Block item in list)
        {
            string bullet = list.IsOrdered ? $"{ordinal}. " : "• ";
            string pad = new(' ', bullet.Length);
            ordinal++;

            List<RenderedLine> itemLines = item is ListItemBlock listItem
                ? RenderBlocks(listItem, baseColor)
                : RenderBlock(item, baseColor);

            for (int i = 0; i < itemLines.Count; i++)
            {
                RenderedLine line = itemLines[i];
                if (line.IsRule)
                {
                    output.Add(line);
                    continue;
                }
                // First line of the item carries the bullet; continuation rows align under it.
                line.Spans.Insert(0, new StyledSpan(i == 0 ? bullet : pad, baseColor, ""));
                output.Add(line);
            }
        }
        return output;
    }

    private static List<RenderedLine> RenderQuote(QuoteBlock quote, string baseColor)
    {
        List<RenderedLine> inner = RenderBlocks(quote, baseColor);
        foreach (RenderedLine line in inner)
        {
            if (line.IsRule)
                continue;
            line.Spans.Insert(0, new StyledSpan("│ ", ChromeForeground, ""));
        }
        return inner;
    }

    private static List<RenderedLine> RenderCodeBlock(CodeBlock code)
    {
        List<RenderedLine> output = [];
        for (int i = 0; i < code.Lines.Count; i++)
        {
            string text = code.Lines.Lines[i].Slice.ToString();
            output.Add(new RenderedLine([new StyledSpan(text, CodeBlockForeground, CodeBackground)], false));
        }
        return output;
    }

    // ---- inline walking ---------------------------------------------------------------

    /// <summary>
    /// Renders an inline container into one-or-more logical lines (hard line breaks split
    /// lines; soft breaks become spaces). <paramref name="decorations"/> is the inherited
    /// style accumulator threaded through nested emphasis.
    /// </summary>
    private static List<RenderedLine> RenderInlines(ContainerInline? container, string baseColor, string decorations)
    {
        List<RenderedLine> lines = [];
        List<StyledSpan> current = [];
        if (container is not null)
            WalkInlines(container, baseColor, decorations, lines, ref current);
        lines.Add(new RenderedLine(current, false));
        return lines;
    }

    private static void WalkInlines(ContainerInline container, string baseColor, string decorations,
        List<RenderedLine> lines, ref List<StyledSpan> current)
    {
        foreach (Inline inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    Append(current, literal.Content.ToString(), baseColor, decorations);
                    break;

                case EmphasisInline emphasis:
                    // DelimiterCount: 1 → italic, 2 → bold, 3 → bold italic. Accumulate onto
                    // whatever decoration is already in effect so nesting composes.
                    bool bold = emphasis.DelimiterCount >= 2;
                    bool italic = emphasis.DelimiterCount % 2 == 1;
                    string nested = decorations;
                    if (bold) nested = Combine(nested, "bold");
                    if (italic) nested = Combine(nested, "italic");
                    WalkInlines(emphasis, baseColor, nested, lines, ref current);
                    break;

                case CodeInline codeInline:
                    // Keep the speaker colour but mark the run with the monospace background.
                    Append(current, codeInline.Content, baseColor, Combine(decorations, CodeBackground));
                    break;

                case LinkInline link:
                    RenderLink(link, baseColor, decorations, lines, ref current);
                    break;

                case AutolinkInline autolink:
                    Append(current, autolink.Url, baseColor, Combine(decorations, "underline"));
                    break;

                case LineBreakInline lineBreak:
                    if (lineBreak.IsHard)
                    {
                        lines.Add(new RenderedLine(current, false));
                        current = [];
                    }
                    else
                    {
                        Append(current, " ", baseColor, decorations);
                    }
                    break;

                // Strip raw HTML inlines (tags carry no terminal-meaningful content); any
                // other ContainerInline still gets its children walked.
                case HtmlInline:
                    break;

                case ContainerInline nestedContainer:
                    WalkInlines(nestedContainer, baseColor, decorations, lines, ref current);
                    break;
            }
        }
    }

    /// <summary>Renders a link's label inline (underlined), then appends the URL dimmed in parentheses when it adds information beyond the label.</summary>
    private static void RenderLink(LinkInline link, string baseColor, string decorations,
        List<RenderedLine> lines, ref List<StyledSpan> current)
    {
        string label = LinkLabel(link);

        if (link.IsImage)
        {
            // No OSC 8, no ASCII art (frozen scope): show alt text + the source URL.
            string alt = label.Length > 0 ? label : "image";
            Append(current, $"[image: {alt}]", ChromeForeground, decorations);
            if (!string.IsNullOrEmpty(link.Url))
                Append(current, $" ({link.Url})", ChromeForeground, "");
            return;
        }

        WalkInlines(link, baseColor, Combine(decorations, "underline"), lines, ref current);
        if (!string.IsNullOrEmpty(link.Url) && !string.Equals(link.Url, label, StringComparison.Ordinal))
            Append(current, $" ({link.Url})", ChromeForeground, "");
    }

    /// <summary>Flattens a link's child inlines to their plain text, used to decide whether the URL is redundant with the label.</summary>
    private static string LinkLabel(LinkInline link)
    {
        StringBuilder sb = new();
        foreach (Inline child in link)
            if (child is LiteralInline literal)
                sb.Append(literal.Content.ToString());
        return sb.ToString();
    }

    private static void Append(List<StyledSpan> spans, string text, string foreground, string decorations)
    {
        if (text.Length > 0)
            spans.Add(new StyledSpan(text, foreground, decorations));
    }

    /// <summary>Joins two decoration token strings with a space, tolerating empties.</summary>
    private static string Combine(string a, string b) =>
        a.Length == 0 ? b : b.Length == 0 ? a : $"{a} {b}";

    // ---- wrapping ---------------------------------------------------------------------

    /// <summary>
    /// Breaks each logical line into single-row <see cref="Markup"/>s at <paramref name="width"/>
    /// visible columns. Spans are sliced across rows as needed; style tokens never count toward
    /// the column budget. Rule lines emit one full-width <c>─</c> row; empty lines emit one blank
    /// row, preserving the budget accounting <see cref="ConsoleUiService.BuildBody"/> depends on.
    /// </summary>
    internal static List<Markup> Wrap(List<RenderedLine> lines, int width)
    {
        width = Math.Max(1, width);
        List<Markup> rows = [];

        foreach (RenderedLine line in lines)
        {
            if (line.IsRule)
            {
                rows.Add(new Markup($"[{RuleStyle}]{new string('─', width)}[/]"));
                continue;
            }

            if (line.Spans.Count == 0)
            {
                rows.Add(new Markup(string.Empty));
                continue;
            }

            StringBuilder sb = new(width + 32);
            int col = 0;
            int rowsBefore = rows.Count;

            foreach (StyledSpan span in line.Spans)
            {
                int consumed = 0;
                while (consumed < span.Text.Length)
                {
                    if (col == width)
                    {
                        rows.Add(new Markup(sb.ToString()));
                        sb.Clear();
                        col = 0;
                    }
                    int take = Math.Min(width - col, span.Text.Length - consumed);
                    AppendSegment(sb, span, span.Text.Substring(consumed, take));
                    consumed += take;
                    col += take;
                }
            }

            // Flush the trailing partial row (or guarantee at least one row for a line whose
            // spans were all empty text).
            if (sb.Length > 0 || rows.Count == rowsBefore)
                rows.Add(new Markup(sb.ToString()));
        }

        return rows;
    }

    /// <summary>Appends one escaped, styled segment to the row builder.</summary>
    private static void AppendSegment(StringBuilder sb, StyledSpan span, string slice)
    {
        string tag = span.Decorations.Length > 0 ? $"{span.Foreground} {span.Decorations}" : span.Foreground;
        sb.Append('[').Append(tag).Append(']').Append(Markup.Escape(slice)).Append("[/]");
    }
}
