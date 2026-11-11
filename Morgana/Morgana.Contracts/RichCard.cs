using System.Text.Json.Serialization;

namespace Morgana.Contracts;

/// <summary>
/// Rich card container for structured visual presentation of complex data such as invoices, profiles or reports.
/// The model generates cards through the SetRichCard tool, which enforces a nesting depth of 3 levels and a maximum of 50 components in total.
/// Components must come from the known dictionary because Cauldron falls back to text for an unknown type.
/// </summary>
/// <param name="Title">The heading of the card.</param>
/// <param name="Subtitle">The line under the heading; null when there is none.</param>
/// <param name="Components">The components of the card in the order they are drawn.</param>
public record RichCard(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("subtitle")] string? Subtitle,
    [property: JsonPropertyName("components")] List<CardComponent> Components
)
{
    // The visual characters that the title line adds around the card title.
    private const int TitleOverhead = 4;
    // The visual characters that the subtitle line adds around the card subtitle.
    private const int SubtitleOverhead = 2;

    /// <summary>Estimated rendering footprint in visual characters that the channel adapter compares against a channel's <c>MaxMessageLength</c> budget.</summary>
    public int EstimateCost() =>
        Title.Length + TitleOverhead
        + (Subtitle?.Length + SubtitleOverhead ?? 0)
        + Components.Sum(component => component.EstimateCost());
}

/// <summary>Base class of every card component, serialised polymorphically with a <c>type</c> discriminator.</summary>
// A new component type needs a record deriving from CardComponent, a JsonDerivedType entry here and a matching Razor component in Cauldron.
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TextBlockComponent), "text_block")]
[JsonDerivedType(typeof(KeyValueComponent), "key_value")]
[JsonDerivedType(typeof(DividerComponent), "divider")]
[JsonDerivedType(typeof(ListComponent), "list")]
[JsonDerivedType(typeof(SectionComponent), "section")]
[JsonDerivedType(typeof(GridComponent), "grid")]
[JsonDerivedType(typeof(BadgeComponent), "badge")]
[JsonDerivedType(typeof(ImageComponent), "image")]
public abstract record CardComponent
{
    /// <summary>Estimated rendering footprint of this component in visual characters.</summary>
    // Conservative by design: overestimating triggers a channel downgrade while underestimating risks overflow past the channel's length budget.
    public abstract int EstimateCost();
}

/// <summary>Free-form text block component for narrative content within cards.</summary>
/// <param name="Content">Text content that may span multiple lines.</param>
/// <param name="Style">How the text is styled, normal by default.</param>
public record TextBlockComponent(
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("style")] TextStyle Style = TextStyle.Normal) : CardComponent
{
    // The visual characters that the text block adds around its content.
    private const int Overhead = 2;

    /// <inheritdoc />
    public override int EstimateCost() => Content.Length + Overhead;
}

/// <summary>Key-value pair component for structured label-value data.</summary>
/// <param name="Key">The label of the pair.</param>
/// <param name="Value">The value of the pair.</param>
/// <param name="Emphasize">True to make the pair stand out from the others.</param>
public record KeyValueComponent(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("value")] string Value,
    [property: JsonPropertyName("emphasize")] bool Emphasize = false) : CardComponent
{
    // The visual characters that the key-value row adds around its key and value.
    private const int Overhead = 4;

    /// <inheritdoc />
    public override int EstimateCost() => Key.Length + Value.Length + Overhead;
}

/// <summary>Divider component that renders as a horizontal line separating logical sections.</summary>
public record DividerComponent : CardComponent
{
    // The visual characters that the horizontal rule occupies.
    private const int Overhead = 4;

    /// <inheritdoc />
    public override int EstimateCost() => Overhead;
}

/// <summary>List component for displaying multiple related items.</summary>
/// <param name="Items">The items in the order they are listed.</param>
/// <param name="Style">How the items are marked, bullets by default.</param>
public record ListComponent(
    [property: JsonPropertyName("items")] List<string> Items,
    [property: JsonPropertyName("style")] ListStyle Style = ListStyle.Bullet) : CardComponent
{
    // The visual characters that the list marker adds in front of each item.
    private const int ItemOverhead = 3;

    /// <inheritdoc />
    public override int EstimateCost() => Items.Sum(item => item.Length + ItemOverhead);
}

/// <summary>
/// Section component that groups child components under a title and can nest other sections.
/// Nesting is limited to a depth of 3 levels, which the SetRichCard tool enforces.
/// </summary>
/// <param name="Title">The heading of the section.</param>
/// <param name="Subtitle">The line under the heading; null when there is none.</param>
/// <param name="Components">The components of the section in the order they are drawn.</param>
public record SectionComponent(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("subtitle")] string? Subtitle,
    [property: JsonPropertyName("components")] List<CardComponent> Components) : CardComponent
{
    // The visual characters that the heading adds around the section title.
    private const int TitleOverhead = 4;
    // The visual characters that the subheading adds around the section subtitle.
    private const int SubtitleOverhead = 2;

    /// <inheritdoc />
    public override int EstimateCost() =>
        Title.Length + TitleOverhead
        + (Subtitle?.Length + SubtitleOverhead ?? 0)
        + Components.Sum(component => component.EstimateCost());
}

/// <summary>Grid component for side-by-side presentation of key-value cells.</summary>
/// <param name="Columns">Number of columns, 2 to 4 recommended.</param>
/// <param name="Items">The cells, filled row by row.</param>
public record GridComponent(
    [property: JsonPropertyName("columns")] int Columns,
    [property: JsonPropertyName("items")] List<GridItem> Items) : CardComponent
{
    // The visual characters that the cell frame adds around each grid key and value.
    private const int ItemOverhead = 4;

    /// <inheritdoc />
    public override int EstimateCost() =>
        Items.Sum(item => item.Key.Length + item.Value.Length + ItemOverhead);
}

/// <summary>Individual grid cell containing a key-value pair.</summary>
/// <param name="Key">The label of the cell.</param>
/// <param name="Value">The value of the cell.</param>
public record GridItem(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("value")] string Value);

/// <summary>Badge component for status indicators and categorical labels.</summary>
/// <param name="Text">The text on the badge.</param>
/// <param name="Variant">The color of the badge, neutral by default.</param>
public record BadgeComponent(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("variant")] BadgeVariant Variant = BadgeVariant.Neutral) : CardComponent
{
    // The visual characters that the badge frame adds around its text.
    private const int Overhead = 2;

    /// <inheritdoc />
    public override int EstimateCost() => Text.Length + Overhead;
}

/// <summary>Image component for displaying visual content from a URL with optional alt text and caption.</summary>
/// <param name="Src">Image URL that must be publicly accessible with HTTPS recommended.</param>
/// <param name="Alt">The text that stands for the image where it cannot be seen; null when there is none.</param>
/// <param name="Caption">The line shown under the image; null when there is none.</param>
/// <param name="Size">How wide the image is drawn, medium by default.</param>
public record ImageComponent(
    [property: JsonPropertyName("src")] string Src,
    [property: JsonPropertyName("alt")] string? Alt = null,
    [property: JsonPropertyName("caption")] string? Caption = null,
    [property: JsonPropertyName("size")] ImageSize Size = ImageSize.Medium) : CardComponent
{
    // The visual characters that the image placeholder occupies.
    private const int PlaceholderOverhead = 10;
    // The visual characters that the caption line adds around the caption.
    private const int CaptionOverhead = 2;

    /// <inheritdoc />
    public override int EstimateCost() =>
        PlaceholderOverhead
        + (Alt?.Length ?? 0)
        + (Caption?.Length + CaptionOverhead ?? 0);
}

/// <summary>Text styling options for TextBlockComponent.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TextStyle>))]
public enum TextStyle
{
    /// <summary>Default body text.</summary>
    [JsonPropertyName("normal")] Normal,
    /// <summary>Bold emphasis.</summary>
    [JsonPropertyName("bold")] Bold,
    /// <summary>De-emphasized / secondary text.</summary>
    [JsonPropertyName("muted")] Muted,
    /// <summary>Reduced font size, typically for captions or footnotes.</summary>
    [JsonPropertyName("small")] Small
}

/// <summary>List presentation styles for ListComponent.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ListStyle>))]
public enum ListStyle
{
    /// <summary>Unordered list with bullet markers.</summary>
    [JsonPropertyName("bullet")] Bullet,
    /// <summary>Ordered list with numeric markers.</summary>
    [JsonPropertyName("numbered")] Numbered,
    /// <summary>List with no markers.</summary>
    [JsonPropertyName("plain")] Plain
}

/// <summary>Badge color variants for BadgeComponent.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<BadgeVariant>))]
public enum BadgeVariant
{
    /// <summary>Positive outcome (green).</summary>
    [JsonPropertyName("success")] Success,
    /// <summary>Cautionary state (amber).</summary>
    [JsonPropertyName("warning")] Warning,
    /// <summary>Failure or blocking condition (red).</summary>
    [JsonPropertyName("error")] Error,
    /// <summary>Informational note (blue).</summary>
    [JsonPropertyName("info")] Info,
    /// <summary>Unstyled / default badge.</summary>
    [JsonPropertyName("neutral")] Neutral
}

/// <summary>Size variants that control the maximum width of displayed images.</summary>
/// <remarks>On mobile devices (&lt;768px) Medium and Large automatically scale to 100% width.</remarks>
[JsonConverter(typeof(JsonStringEnumConverter<ImageSize>))]
public enum ImageSize
{
    /// <summary>200px max-width — thumbnails, icons, avatars.</summary>
    [JsonPropertyName("small")] Small,
    /// <summary>400px max-width — default for most images.</summary>
    [JsonPropertyName("medium")] Medium,
    /// <summary>600px max-width — detailed images or diagrams.</summary>
    [JsonPropertyName("large")] Large,
    /// <summary>100% container width — banners and wide images.</summary>
    [JsonPropertyName("full")] Full
}