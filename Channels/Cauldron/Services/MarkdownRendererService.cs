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
    // UseEmojiAndSmiley resolves :shortcode: emoji (e.g. :white_check_mark: → ✅) to real glyphs,
    // matching the Spectre Emoji.Replace step in the Grimoire/Rune renderers — a browser no more
    // expands GitHub-style shortcodes than a terminal does. enableSmiley:false is deliberate: we
    // want ONLY the :name: form, not ASCII smiley conversion (:) → 😃), which would mangle
    // legitimate prose and code. The single shared Pipeline means this covers chat prose (ToHtml),
    // inline card fields (ToInlineHtml) and the ToPlainText strip (StripMarkdown) in one place.
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

        if (html.StartsWith("<p>") && html.EndsWith("</p>"))
            html = html[3..^4];

        return new MarkupString(html);
    }
}