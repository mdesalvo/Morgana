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
/// Additionally, <see cref="SanitizeRichCard"/> walks the entire <see cref="RichCard"/>
/// component tree and strips inline Markdown syntax from every text property, so that
/// individual Razor components don't need to be Markdown-aware.
/// </summary>
public static class MarkdownRendererService
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().Build();

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

    /// <summary>
    /// Walks the <see cref="RichCard"/> component tree and converts inline Markdown to
    /// plain text in every string property.  Call this once before rendering the card
    /// so that downstream Razor components receive clean content.
    /// </summary>
    public static RichCard SanitizeRichCard(RichCard card) =>
        card with
        {
            Title = StripMarkdown(card.Title),
            Subtitle = StripMarkdown(card.Subtitle),
            Components = SanitizeComponents(card.Components)
        };

    // The wire contracts are immutable records, so sanitization rebuilds the component
    // tree functionally (`with`) instead of mutating in place.
    private static List<CardComponent> SanitizeComponents(List<CardComponent> components) =>
        components.Select(SanitizeComponent).ToList();

    private static CardComponent SanitizeComponent(CardComponent component) => component switch
    {
        TextBlockComponent textBlock => textBlock with { Content = StripMarkdown(textBlock.Content) },

        KeyValueComponent keyValue => keyValue with
        {
            Key = StripMarkdown(keyValue.Key),
            Value = StripMarkdown(keyValue.Value)
        },

        BadgeComponent badge => badge with { Text = StripMarkdown(badge.Text) },

        ListComponent list => list with { Items = list.Items.Select(StripMarkdown).ToList() },

        GridComponent grid => grid with
        {
            Items = grid.Items
                .Select(item => item with { Key = StripMarkdown(item.Key), Value = StripMarkdown(item.Value) })
                .ToList()
        },

        SectionComponent section => section with
        {
            Title = StripMarkdown(section.Title),
            Subtitle = StripMarkdown(section.Subtitle),
            Components = SanitizeComponents(section.Components)
        },

        ImageComponent image => image with { Caption = StripMarkdown(image.Caption) },

        _ => component
    };

    private static string StripMarkdown(string? text) =>
        string.IsNullOrEmpty(text) ? text ?? string.Empty : Markdown.ToPlainText(text, Pipeline).Trim();
}