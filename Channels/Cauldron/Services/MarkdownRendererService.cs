using Morgana.Contracts;
using Markdig;
using Microsoft.AspNetCore.Components;

namespace Cauldron.Services;

/// <summary>
/// Centralised Markdown-to-HTML service used by chat message rendering and rich card
/// components.  Two rendering modes are exposed:
/// <list type="bullet">
///   <item><term><see cref="ToHtml"/></term>
///     <description>Block-level rendering (paragraphs, headings, lists, code blocks).
///     Suitable for full chat messages.</description></item>
///   <item><term><see cref="ToInlineHtml"/></term>
///     <description>Inline-only rendering (bold, italic, code, links) without the
///     outer &lt;p&gt; wrapper. Suitable for rich card fields where the surrounding
///     component already provides block-level structure.</description></item>
/// </list>
/// Rich card leaf components (list items, text blocks, key/value pairs, badges, grid cells,
/// section titles, captions) render their own text through <see cref="ToInlineHtml"/>, so a
/// card's Markdown surfaces as real formatting (e.g. <c>**bold**</c> → <c>&lt;strong&gt;</c>)
/// rather than being stripped away or leaking as raw <c>**</c> markers.
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