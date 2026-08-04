using Morgana.Contracts;
using Markdig;
using Microsoft.AspNetCore.Components;

namespace Cauldron.Services;

/// <summary>
/// Markdown-to-HTML service: ToHtml for block-level (chat messages), ToInlineHtml for
/// inline-only without &lt;p&gt; wrapper (rich card fields). Rich card components render
/// through ToInlineHtml so Markdown surfaces as formatting, not raw markers.
/// </summary>
public static class MarkdownRendererService
{
    // Resolves :shortcode: emoji to real glyphs (:white_check_mark: → ✅), which a browser no
    // more expands on its own than a terminal does. Smileys stay off on purpose: converting
    // :) into 😃 would mangle legitimate prose and code. Shared by both renderers below.
    private static readonly MarkdownPipeline Pipeline =
        new MarkdownPipelineBuilder().UseEmojiAndSmiley(enableSmileys: false).Build();

    /// <summary>
    /// Renders Markdown as block-level HTML (paragraphs, lists, headings, etc.).
    /// </summary>
    public static MarkupString ToHtml(string? text) =>
        new MarkupString(Markdown.ToHtml(text ?? string.Empty, Pipeline));

    /// <summary>
    /// Renders Markdown as inline HTML, stripping the outer &lt;p&gt; wrapper that
    /// Markdig adds by default.  Useful inside rich card components where block-level
    /// wrapping would break the layout.
    /// </summary>
    public static MarkupString ToInlineHtml(string? text)
    {
        string html = Markdown.ToHtml(text ?? string.Empty, Pipeline).Trim();

        // Markdig always wraps in a paragraph. Inside a card field that block-level wrapper
        // breaks the layout, so it is peeled off when it is the only one.
        if (html.StartsWith("<p>") && html.EndsWith("</p>"))
            html = html[3..^4];

        return new MarkupString(html);
    }
}