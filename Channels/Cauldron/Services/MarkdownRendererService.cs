using AngleSharp.Html.Dom;
using Cauldron.Interfaces;
using Ganss.Xss;
using Markdig;
using Microsoft.AspNetCore.Components;

namespace Cauldron.Services;

/// <summary>Converts message and rich-card text from Markdown to HTML for display, blocking HTML injection and the images outside the listed domains.</summary>
public class MarkdownRendererService : IMarkdownRendererService
{
    /// <summary>
    /// The Markdown → HTML rules this instance renders with: emoji shortcodes, GFM pipe tables and raw HTML tags disabled.
    /// Without that last part, a bare-looking tag typed in ordinary prose (e.g. "List<string>", "<Component>") would
    /// reach sanitizer as an unclosed HTML element, which silently swallows the rest of the message as that element's
    /// content before dropping it — truncating legitimate text, not just blocking attacks. Disabling HTML here turns
    /// those into harmless literal text instead.
    /// </summary>
    private readonly MarkdownPipeline pipeline;

    /// <summary>Removes dangerous tags, attributes and schemes from the HTML Markdig produces and replaces with their alternative text the images outside the listed domains.</summary>
    private readonly HtmlSanitizer sanitizer = new HtmlSanitizer();

    /// <summary>Builds the Markdig pipeline once for reuse across every render call.</summary>
    public MarkdownRendererService(ITrustedImageDomainService trustedImageDomains)
    {
        // Resolves :shortcode: emoji to real glyphs (:white_check_mark: → ✅), which a browser no
        // more expands on its own than a terminal does. Smileys stay off on purpose: converting
        // :) into 😃 would mangle legitimate prose and code. UsePipeTables() is GFM table syntax
        // (`| a | b |`) — off by default in Markdig, without it the pipes render as literal text
        // instead of a <table>. DisableHtml() is the truncation-bug fix described on the pipeline
        // field above.
        pipeline = new MarkdownPipelineBuilder()
            .UseEmojiAndSmiley(enableSmileys: false)
            .UsePipeTables()
            .DisableHtml()
            .Build();

        // The library's default scheme whitelist (http, https, mailto, tel, callto, cid, xmpp) is kept
        // as shipped: none of them executes script the way javascript: or data: do. The handler holds
        // no state of its own, so concurrent Sanitize calls from different circuits stay safe.
        // An image outside the listed domains is replaced by its alternative text: the reader learns
        // what was meant to be there and the browser gets no URL to load (the CSP would refuse it
        // anyway and leave a broken icon).
        sanitizer.PostProcessNode += (sender, nodeEvent) =>
        {
            // Only images are judged here: every other node passes through untouched.
            if (nodeEvent.Node is IHtmlImageElement image && !trustedImageDomains.IsServedFromTrustedDomain(image.GetAttribute("src")))
                nodeEvent.ReplacementNodes.Add(nodeEvent.Document.CreateTextNode(image.AlternativeText ?? string.Empty));
        };
    }

    /// <summary>Renders Markdown as block-level HTML (paragraphs, lists, headings, etc.).</summary>
    public MarkupString ToHtml(string? text)
        // Paragraphs and lists stay as Markdig wraps them.
        => new MarkupString(Render(text));

    /// <summary>
    /// Renders Markdown as inline HTML, stripping the outer &lt;p&gt; wrapper that Markdig adds by default.
    /// Useful inside rich card components where block-level wrapping would break the layout.
    /// </summary>
    public MarkupString ToInlineHtml(string? text)
    {
        // Trim first: Markdig's HTML renderer emits a trailing newline after the closing </p>,
        // which would make the EndsWith("</p>") check below fail and skip the unwrap.
        string html = Render(text).Trim();

        // Markdig always wraps in a paragraph. Inside a card field that block-level wrapper
        // breaks the layout, so it is peeled off when it is the only one.
        if (html.StartsWith("<p>", StringComparison.Ordinal) && html.EndsWith("</p>", StringComparison.Ordinal))
            html = html[3..^4];

        // The text ready to sit inside a card field.
        return new MarkupString(html);
    }

    /// <summary>
    /// Turns Markdown into HTML and sanitizes it, for both <see cref="ToHtml"/> and <see cref="ToInlineHtml"/>.
    /// No path renders unsanitized HTML.
    /// </summary>
    private string Render(string? text)
        // Absent text renders as nothing.
        => sanitizer.Sanitize(Markdown.ToHtml(text ?? string.Empty, pipeline));
}