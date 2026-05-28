using System.Text.Json.Serialization;

namespace Morgana.Contracts;

/// <summary>
/// Rich card container for structured visual presentation of complex data.
/// Used to render information (invoices, profiles, reports) with visual hierarchy
/// instead of plain text walls.
/// </summary>
/// <param name="Title">Main title of the card</param>
/// <param name="Subtitle">Optional subtitle or secondary information</param>
/// <param name="Components">Array of visual components to render</param>
/// <remarks>
/// <para><strong>Usage:</strong></para>
/// <para>LLM generates rich cards via SetRichCard tool when presenting structured data.
/// Cards flow through actor pipeline (Agent → Router → Supervisor → Manager → SignalR → Cauldron).</para>
/// <para><strong>Constraints:</strong></para>
/// <list type="bullet">
/// <item>Maximum nesting depth: 3 levels (enforced by SetRichCard tool)</item>
/// <item>Maximum 50 components total (prevents abuse)</item>
/// <item>Components must be from known dictionary (unknown types fallback to text in Cauldron)</item>
/// </list>
/// </remarks>
public record RichCard(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("subtitle")] string? Subtitle,
    [property: JsonPropertyName("components")] List<CardComponent> Components
)
{
    private const int TitleOverhead = 4;
    private const int SubtitleOverhead = 2;

    /// <summary>
    /// Estimated rendering footprint in visual characters. Used by the channel adapter
    /// to decide whether the card fits inside a channel's <c>MaxMessageLength</c> budget.
    /// Conservative: prefer overestimating (trigger a downgrade) over letting an
    /// oversized payload slip through.
    /// </summary>
    public int EstimateCost() =>
        Title.Length + TitleOverhead
        + (Subtitle?.Length + SubtitleOverhead ?? 0)
        + Components.Sum(component => component.EstimateCost());
}

/// <summary>
/// Base class for all card components.
/// Uses JSON polymorphic serialization for type discrimination.
/// </summary>
/// <remarks>
/// <para><strong>Component Dictionary:</strong></para>
/// <list type="bullet">
/// <item><term>text_block</term><description>Free-form narrative text</description></item>
/// <item><term>key_value</term><description>Label-value pairs for structured data</description></item>
/// <item><term>divider</term><description>Visual separator between sections</description></item>
/// <item><term>list</term><description>Bulleted, numbered, or plain item lists</description></item>
/// <item><term>section</term><description>Nestable grouping with title/subtitle</description></item>
/// <item><term>grid</term><description>2-4 column layout for side-by-side data</description></item>
/// <item><term>badge</term><description>Status indicators (success, warning, error, info, neutral)</description></item>
/// <item><term>image</term><description>Multimedia content of type image</description></item>
/// </list>
/// <para><strong>Extensibility:</strong></para>
/// <para>Implementers can add new component types by:</para>
/// <list type="number">
/// <item>Adding new record inheriting from CardComponent</item>
/// <item>Adding JsonDerivedType attribute to CardComponent</item>
/// <item>Creating corresponding Razor component in Cauldron</item>
/// </list>
/// </remarks>
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
    /// <summary>
    /// Estimated rendering footprint of this component in visual characters.
    /// Conservative by design: overestimating triggers a channel downgrade, while
    /// underestimating risks overflow past the channel's length budget.
    /// </summary>
    public abstract int EstimateCost();
}

/// <summary>
/// Free-form text block component for narrative content within cards.
/// </summary>
/// <param name="Content">Text content (supports multiline)</param>
/// <param name="Style">Visual styling for the text</param>
public record TextBlockComponent(
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("style")] TextStyle Style = TextStyle.Normal
) : CardComponent
{
    private const int Overhead = 2;

    /// <inheritdoc />
    public override int EstimateCost() => Content.Length + Overhead;
}

/// <summary>
/// Key-value pair component for structured label-value data.
/// </summary>
/// <param name="Key">Label/field name (e.g., "Cliente", "Totale")</param>
/// <param name="Value">Corresponding value (e.g., "Acme Corp", "€1.250,00")</param>
/// <param name="Emphasize">True to highlight this pair visually (e.g., bold, larger font)</param>
public record KeyValueComponent(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("value")] string Value,
    [property: JsonPropertyName("emphasize")] bool Emphasize = false
) : CardComponent
{
    private const int Overhead = 4;

    /// <inheritdoc />
    public override int EstimateCost() => Key.Length + Value.Length + Overhead;
}

/// <summary>
/// Visual divider/separator component.
/// Renders as horizontal line to separate logical sections.
/// </summary>
public record DividerComponent : CardComponent
{
    private const int Overhead = 4;

    /// <inheritdoc />
    public override int EstimateCost() => Overhead;
}

/// <summary>
/// List component for displaying multiple related items.
/// </summary>
/// <param name="Items">Array of text items to display</param>
/// <param name="Style">List presentation style (bullet, numbered, plain)</param>
public record ListComponent(
    [property: JsonPropertyName("items")] List<string> Items,
    [property: JsonPropertyName("style")] ListStyle Style = ListStyle.Bullet
) : CardComponent
{
    private const int ItemOverhead = 3;

    /// <inheritdoc />
    public override int EstimateCost() => Items.Sum(item => item.Length + ItemOverhead);
}

/// <summary>
/// Section component for logical grouping with nesting support.
/// Enables hierarchical organization of card content (max depth: 3).
/// </summary>
/// <param name="Title">Section title/heading</param>
/// <param name="Subtitle">Optional section subtitle</param>
/// <param name="Components">Child components within this section (can include nested sections)</param>
public record SectionComponent(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("subtitle")] string? Subtitle,
    [property: JsonPropertyName("components")] List<CardComponent> Components
) : CardComponent
{
    private const int TitleOverhead = 4;
    private const int SubtitleOverhead = 2;

    /// <inheritdoc />
    public override int EstimateCost() =>
        Title.Length + TitleOverhead
        + (Subtitle?.Length + SubtitleOverhead ?? 0)
        + Components.Sum(component => component.EstimateCost());
}

/// <summary>
/// Grid component for side-by-side data presentation.
/// </summary>
/// <param name="Columns">Number of columns (2-4 recommended)</param>
/// <param name="Items">Grid cells with key-value pairs</param>
public record GridComponent(
    [property: JsonPropertyName("columns")] int Columns,
    [property: JsonPropertyName("items")] List<GridItem> Items
) : CardComponent
{
    private const int ItemOverhead = 4;

    /// <inheritdoc />
    public override int EstimateCost() =>
        Items.Sum(item => item.Key.Length + item.Value.Length + ItemOverhead);
}

/// <summary>
/// Individual grid cell containing a key-value pair.
/// </summary>
/// <param name="Key">Label for this grid cell</param>
/// <param name="Value">Value for this grid cell</param>
public record GridItem(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("value")] string Value
);

/// <summary>
/// Badge component for status indicators and categorical labels.
/// </summary>
/// <param name="Text">Badge text (e.g., "Pagata", "In sospeso")</param>
/// <param name="Variant">Visual variant determining color/style</param>
public record BadgeComponent(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("variant")] BadgeVariant Variant = BadgeVariant.Neutral
) : CardComponent
{
    private const int Overhead = 2;

    /// <inheritdoc />
    public override int EstimateCost() => Text.Length + Overhead;
}

/// <summary>
/// Image component for displaying visual content from URLs.
/// Supports captions, alt text, and size variants.
/// </summary>
/// <param name="Src">Image URL (must be publicly accessible, HTTPS recommended)</param>
/// <param name="Alt">Alternative text for accessibility (optional but recommended)</param>
/// <param name="Caption">Optional caption displayed below the image</param>
/// <param name="Size">Display size variant (small, medium, large, or full)</param>
public record ImageComponent(
    [property: JsonPropertyName("src")] string Src,
    [property: JsonPropertyName("alt")] string? Alt = null,
    [property: JsonPropertyName("caption")] string? Caption = null,
    [property: JsonPropertyName("size")] ImageSize Size = ImageSize.Medium
) : CardComponent
{
    private const int PlaceholderOverhead = 10;
    private const int CaptionOverhead = 2;

    /// <inheritdoc />
    public override int EstimateCost() =>
        PlaceholderOverhead
        + (Alt?.Length ?? 0)
        + (Caption?.Length + CaptionOverhead ?? 0);
}

// Enums for component styling

/// <summary>
/// Text styling options for TextBlockComponent.
/// </summary>
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

/// <summary>
/// List presentation styles for ListComponent.
/// </summary>
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

/// <summary>
/// Badge color variants for BadgeComponent.
/// </summary>
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

/// <summary>
/// Size variants for image components.
/// Controls maximum width of displayed images with responsive behavior.
/// </summary>
/// <remarks>
/// <para><strong>Size Guidelines:</strong></para>
/// <list type="bullet">
/// <item><term>Small</term><description>200px max-width - for thumbnails, icons, small avatars</description></item>
/// <item><term>Medium</term><description>400px max-width - default, good for most images</description></item>
/// <item><term>Large</term><description>600px max-width - for detailed images, diagrams</description></item>
/// <item><term>Full</term><description>100% container width - for banners, wide images</description></item>
/// </list>
/// <para>On mobile devices (&lt;768px), Medium and Large automatically scale to 100% width.</para>
/// </remarks>
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