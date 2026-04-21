using Cauldron.Messages.Contracts;
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
    public static RichCard SanitizeRichCard(RichCard card)
    {
        card.Title = StripMarkdown(card.Title);
        card.Subtitle = StripMarkdown(card.Subtitle);
        SanitizeComponents(card.Components);
        return card;
    }

    private static void SanitizeComponents(List<CardComponent> components)
    {
        foreach (CardComponent component in components)
        {
            switch (component)
            {
                case TextBlockComponent textBlock:
                    textBlock.Content = StripMarkdown(textBlock.Content);
                    break;

                case KeyValueComponent keyValue:
                    keyValue.Key = StripMarkdown(keyValue.Key);
                    keyValue.Value = StripMarkdown(keyValue.Value);
                    break;

                case BadgeComponent badge:
                    badge.Text = StripMarkdown(badge.Text);
                    break;

                case ListComponent list:
                    for (int i = 0; i < list.Items.Count; i++)
                        list.Items[i] = StripMarkdown(list.Items[i]);
                    break;

                case GridComponent grid:
                    foreach (GridItem item in grid.Items)
                    {
                        item.Key = StripMarkdown(item.Key);
                        item.Value = StripMarkdown(item.Value);
                    }
                    break;

                case SectionComponent section:
                    section.Title = StripMarkdown(section.Title);
                    section.Subtitle = StripMarkdown(section.Subtitle);
                    SanitizeComponents(section.Components);
                    break;

                case ImageComponent image:
                    image.Caption = StripMarkdown(image.Caption);
                    break;
            }
        }
    }

    private static string StripMarkdown(string? text) =>
        string.IsNullOrEmpty(text) ? text ?? string.Empty : Markdown.ToPlainText(text, Pipeline).Trim();
}