using Microsoft.AspNetCore.Components;

namespace Cauldron.Interfaces;

/// <summary>
/// Markdown-to-HTML rendering contract: ToHtml for block-level (chat messages), ToInlineHtml
/// for inline-only without a &lt;p&gt; wrapper (rich card fields).
/// </summary>
public interface IMarkdownRendererService
{
    /// <summary>
    /// Renders Markdown as block-level HTML (paragraphs, lists, headings, etc.).
    /// </summary>
    MarkupString ToHtml(string? text);

    /// <summary>
    /// Renders Markdown as inline HTML, stripping the outer &lt;p&gt; wrapper that
    /// Markdig adds by default. Useful inside rich card components where block-level
    /// wrapping would break the layout.
    /// </summary>
    MarkupString ToInlineHtml(string? text);
}