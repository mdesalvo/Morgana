using Cauldron.Interfaces;
using Ganss.Xss;
using Markdig;
using Microsoft.AspNetCore.Components;

namespace Cauldron.Services;

/// <summary>
/// Converts message and rich-card text from Markdown to HTML for display, blocking the
/// HTML-injection vectors that raw Markdown would otherwise let through untouched.
/// </summary>
public class MarkdownRendererService : IMarkdownRendererService
{
    /// <summary>
    /// The Markdown → HTML rules this instance renders with: emoji shortcodes, and raw HTML
    /// tags disabled. Without that second part, a bare-looking tag typed in ordinary prose
    /// (e.g. "List&lt;string&gt;", "&lt;Component&gt;") would reach <see cref="sanitizer"/> as
    /// an unclosed HTML element, which silently swallows the rest of the message as that
    /// element's content before dropping it — truncating legitimate text, not just blocking
    /// attacks. Disabling HTML here turns those into harmless literal text instead.
    /// </summary>
    private readonly MarkdownPipeline pipeline;

    /// <summary>
    /// Strips dangerous tags, attributes and URL schemes from the HTML Markdig produces —
    /// script tags, event-handler attributes (onerror, onclick, ...), CSS-based vectors, and
    /// any link/image whose URL scheme falls outside the library's own default whitelist
    /// (http, https, mailto, tel, callto, cid, xmpp — none of them script-executing or
    /// origin-redirecting the way javascript:/data: are, so left as shipped rather than
    /// narrowed). Reused across every render call: never reconfigured after construction, so
    /// concurrent <c>Sanitize</c> calls from different circuits are safe.
    /// </summary>
    private readonly HtmlSanitizer sanitizer = new HtmlSanitizer();

    /// <summary>
    /// Builds the Markdig pipeline once for reuse across every render call.
    /// </summary>
    public MarkdownRendererService()
    {
        // Resolves :shortcode: emoji to real glyphs (:white_check_mark: → ✅), which a browser no
        // more expands on its own than a terminal does. Smileys stay off on purpose: converting
        // :) into 😃 would mangle legitimate prose and code. DisableHtml() is the truncation-bug
        // fix described on the pipeline field above.
        pipeline = new MarkdownPipelineBuilder().UseEmojiAndSmiley(enableSmileys: false).DisableHtml().Build();
    }

    /// <summary>
    /// Renders Markdown as block-level HTML (paragraphs, lists, headings, etc.).
    /// </summary>
    public MarkupString ToHtml(string? text) => new MarkupString(Render(text));

    /// <summary>
    /// Renders Markdown as inline HTML, stripping the outer &lt;p&gt; wrapper that
    /// Markdig adds by default.  Useful inside rich card components where block-level
    /// wrapping would break the layout.
    /// </summary>
    public MarkupString ToInlineHtml(string? text)
    {
        // Trim first: Markdig's HTML renderer emits a trailing newline after the closing </p>,
        // which would make the EndsWith("</p>") check below fail and skip the unwrap.
        string html = Render(text).Trim();

        // Markdig always wraps in a paragraph. Inside a card field that block-level wrapper
        // breaks the layout, so it is peeled off when it is the only one.
        if (html.StartsWith("<p>") && html.EndsWith("</p>"))
            html = html[3..^4];

        return new MarkupString(html);
    }

    /// <summary>
    /// Shared rendering path for both <see cref="ToHtml"/> and <see cref="ToInlineHtml"/>:
    /// Markdig turns the Markdown into HTML, then that HTML is always sanitized before display —
    /// there is no toggle to skip it, unsanitized rendering is not a supported mode.
    /// </summary>
    private string Render(string? text) => sanitizer.Sanitize(Markdown.ToHtml(text ?? string.Empty, pipeline));
}