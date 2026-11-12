using Cauldron.Interfaces;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Morgana.Contracts;

namespace Cauldron.Services;

/// <summary>Admits an image only when it is served over https from a domain listed in configuration and reports each one a delivered reply carries from elsewhere.</summary>
public class TrustedImageDomainService : ITrustedImageDomainService
{
    /// <summary>The bare host names images may be loaded from.</summary>
    private readonly string[] trustedImageDomains;

    /// <summary>Receives the withheld images as Warning lines.</summary>
    private readonly ILogger logger;

    public TrustedImageDomainService(IConfiguration configuration, ILogger logger)
    {
        // Empty when nothing is configured: no image is then trusted
        trustedImageDomains = configuration.GetSection("Cauldron:TrustedImageDomains").Get<string[]>() ?? [];
        this.logger = logger;
    }

    /// <summary>Returns true only for an absolute https URL on the default port whose host is a listed domain.</summary>
    public bool IsServedFromTrustedDomain(string? imageUrl)
    {
        // A relative URL is refused: no content of Morgana has a reason to point at Cauldron's own
        // resources. https is required because http travels in clear and the CSP refuses it anyway.
        // The default port is required because the CSP lists the host without a port and would refuse
        // any other. The host must match exactly because the listed domain is the one the browser asks
        // for the image and so the one that receives the URL with whatever data was put in it.
        return Uri.TryCreate(imageUrl, UriKind.Absolute, out Uri? imageUri)
               && imageUri.Scheme == Uri.UriSchemeHttps
               && imageUri.IsDefaultPort
               && trustedImageDomains.Contains(imageUri.Host, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Records a metric and a Warning for each image of the reply that does not come from a listed domain.</summary>
    public void ReportImagesFromUntrustedDomains(ChannelMessage reply)
    {
        // The Markdown strings and the card image sources that the reply carries, gathered from its text and its card
        List<string> markdownTexts = [reply.Text];
        List<string> cardImageSources = [];
        if (reply.RichCard is not null)
        {
            // The card header is rendered as Markdown like the text of the reply
            markdownTexts.Add(reply.RichCard.Title);
            if (reply.RichCard.Subtitle is not null)
                markdownTexts.Add(reply.RichCard.Subtitle);
            CollectCardImages(reply.RichCard.Components, markdownTexts, cardImageSources);
        }

        // Each image written in Markdown is one event when its domain is not listed
        List<(string Door, string Url)> withheldImages = [];
        foreach (string markdownText in markdownTexts.Where(text => !string.IsNullOrEmpty(text)))
        {
            IEnumerable<string> markdownImageUrls = Markdown.Parse(markdownText)
                .Descendants<LinkInline>()
                .Where(link => link.IsImage)
                .Select(link => link.Url ?? string.Empty);
            withheldImages.AddRange(markdownImageUrls
                .Where(url => !IsServedFromTrustedDomain(url))
                .Select(url => (Telemetry.SecurityDoors.MarkdownImage, url)));
        }

        // Each card image component is one event when its domain is not listed
        withheldImages.AddRange(cardImageSources
            .Where(source => !IsServedFromTrustedDomain(source))
            .Select(source => (Telemetry.SecurityDoors.CardImage, source)));

        foreach ((string Door, string Url) withheldImage in withheldImages)
        {
            // Only the host reaches the log: the path and the query are where an exfiltration URL puts its data.
            // A rooted path parses as an absolute file URI on Unix, whose empty host would name nobody.
            string imageHost = Uri.TryCreate(withheldImage.Url, UriKind.Absolute, out Uri? imageUri) && imageUri.Host.Length > 0
                ? imageUri.Host
                : "a relative or malformed URL";

            // The metric is tagged by door and outcome alone because the domain would be unbounded
            Telemetry.SecurityEvents.Add(1,
                new KeyValuePair<string, object?>(Telemetry.SecurityDoor, withheldImage.Door),
                new KeyValuePair<string, object?>(Telemetry.SecurityOutcome, Telemetry.SecurityOutcomes.Withheld));
            logger.LogWarning("Withheld a {Door} from {ImageHost} in a reply of conversation {ConversationId}", withheldImage.Door, imageHost, reply.ConversationId);
        }
    }

    /// <summary>Gathers the Markdown strings and the image sources of the components, descending into sections.</summary>
    private static void CollectCardImages(IEnumerable<CardComponent> components, List<string> markdownTexts, List<string> cardImageSources)
    {
        foreach (CardComponent component in components)
        {
            // Components of a kind that is not listed here carry no text that is rendered as Markdown
            switch (component)
            {
                // The content is rendered as Markdown
                case TextBlockComponent textBlock:
                    markdownTexts.Add(textBlock.Content);
                    break;
                // Key and value are both rendered as Markdown
                case KeyValueComponent keyValue:
                    markdownTexts.Add(keyValue.Key);
                    markdownTexts.Add(keyValue.Value);
                    break;
                // Each item is rendered as Markdown
                case ListComponent list:
                    markdownTexts.AddRange(list.Items);
                    break;
                // The header is rendered as Markdown and the nested components are inspected in turn
                case SectionComponent section:
                    markdownTexts.Add(section.Title);
                    if (section.Subtitle is not null)
                        markdownTexts.Add(section.Subtitle);
                    CollectCardImages(section.Components, markdownTexts, cardImageSources);
                    break;
                // Key and value of each cell are rendered as Markdown
                case GridComponent grid:
                    foreach (GridItem gridItem in grid.Items)
                    {
                        markdownTexts.Add(gridItem.Key);
                        markdownTexts.Add(gridItem.Value);
                    }
                    break;
                // The text is rendered as Markdown
                case BadgeComponent badge:
                    markdownTexts.Add(badge.Text);
                    break;
                // The caption is rendered as Markdown while the source is an image of its own
                case ImageComponent image:
                    if (image.Caption is not null)
                        markdownTexts.Add(image.Caption);
                    cardImageSources.Add(image.Src);
                    break;
            }
        }
    }
}