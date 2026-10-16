using System.ComponentModel;
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
/// <para>LLM generates rich cards as the card argument of the CloseTurn tool, whose schema is derived from these types.
/// Cards flow through actor pipeline (Agent → Router → Supervisor → Manager → SignalR → Cauldron).</para>
/// <para><strong>Constraints:</strong></para>
/// <list type="bullet">
/// <item>Maximum nesting depth: 3 levels (enforced by the CloseTurn tool)</item>
/// <item>Maximum 50 components total (prevents abuse)</item>
/// <item>Components must be from known dictionary (unknown types fallback to text in Cauldron)</item>
/// </list>
/// </remarks>
[Description("A card presenting the structured data of the turn.")]
public record RichCard(
    [property: JsonPropertyName("title")]
    [property: Description("The main title of the card.")]
    string Title,

    [property: JsonPropertyName("subtitle")]
    [property: Description("A secondary line under the title.")]
    string? Subtitle,

    [property: JsonPropertyName("components")]
    [property: Description("The components of the card in reading order.")]
    List<CardComponent> Components
)
{
    /// <summary>The title counts this many characters beyond its text when the card is measured against a channel's length budget.</summary>
    private const int TitleOverhead = 4;

    /// <summary>The subtitle counts this many characters beyond its text when the card is measured against a channel's length budget.</summary>
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
[Description("One component of a card; its type field tells which kind it is.")]
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
[Description("Prose: free-form narrative text.")]
public record TextBlockComponent(
    [property: JsonPropertyName("content")]
    [property: Description("The text to show; it may span several lines.")]
    string Content,

    [property: JsonPropertyName("style")]
    [property: Description("How the text is styled.")]
    TextStyle Style = TextStyle.Normal) : CardComponent
{
    /// <summary>A text block counts this many characters beyond its text when the card is measured against a channel's length budget.</summary>
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
[Description("One labeled field.")]
public record KeyValueComponent(
    [property: JsonPropertyName("key")]
    [property: Description("The label of the field.")]
    string Key,

    [property: JsonPropertyName("value")]
    [property: Description("The value of the field.")]
    string Value,

    [property: JsonPropertyName("emphasize")]
    [property: Description("True to highlight the pair, for the datum that matters most.")]
    bool Emphasize = false) : CardComponent
{
    /// <summary>A key-value pair counts this many characters beyond its key and value when the card is measured against a channel's length budget.</summary>
    private const int Overhead = 4;

    /// <inheritdoc />
    public override int EstimateCost() => Key.Length + Value.Length + Overhead;
}

/// <summary>
/// Visual divider/separator component.
/// Renders as horizontal line to separate logical sections.
/// </summary>
[Description("A separator placed between groups of components.")]
public record DividerComponent : CardComponent
{
    /// <summary>A divider counts this many characters when the card is measured against a channel's length budget.</summary>
    private const int Overhead = 4;

    /// <inheritdoc />
    public override int EstimateCost() => Overhead;
}

/// <summary>
/// List component for displaying multiple related items.
/// </summary>
/// <param name="Items">Array of text items to display</param>
/// <param name="Style">List presentation style (bullet, numbered, plain)</param>
[Description("An enumeration of items.")]
public record ListComponent(
    [property: JsonPropertyName("items")]
    [property: Description("The texts of the list, one per item.")]
    List<string> Items,

    [property: JsonPropertyName("style")]
    [property: Description("Markers shown before each item.")]
    ListStyle Style = ListStyle.Bullet) : CardComponent
{
    /// <summary>Each list item counts this many characters beyond its text when the card is measured against a channel's length budget.</summary>
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
[Description("A group of related components under a title; may nest further sections.")]
public record SectionComponent(
    [property: JsonPropertyName("title")]
    [property: Description("The heading of the group.")]
    string Title,

    [property: JsonPropertyName("subtitle")]
    [property: Description("A secondary line under the section title.")]
    string? Subtitle,

    [property: JsonPropertyName("components")]
    [property: Description("The components grouped by the section.")]
    List<CardComponent> Components) : CardComponent
{
    /// <summary>The section title counts this many characters beyond its text when the card is measured against a channel's length budget.</summary>
    private const int TitleOverhead = 4;

    /// <summary>The section subtitle counts this many characters beyond its text when the card is measured against a channel's length budget.</summary>
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
[Description("Several homogeneous key-value pairs laid out side by side.")]
public record GridComponent(
    [property: JsonPropertyName("columns")]
    [property: Description("How many pairs sit on one row: 2 to 4.")]
    int Columns,

    [property: JsonPropertyName("items")]
    [property: Description("The key-value pairs of the grid.")]
    List<GridItem> Items) : CardComponent
{
    /// <summary>Each grid pair counts this many characters beyond its key and value when the card is measured against a channel's length budget.</summary>
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
[Description("One key-value pair of a grid.")]
public record GridItem(
    [property: JsonPropertyName("key")]
    [property: Description("The label of the pair.")]
    string Key,

    [property: JsonPropertyName("value")]
    [property: Description("The value of the pair.")]
    string Value);

/// <summary>
/// Badge component for status indicators and categorical labels.
/// </summary>
/// <param name="Text">Badge text (e.g., "Pagata", "In sospeso")</param>
/// <param name="Variant">Visual variant determining color/style</param>
[Description("A status shown as a colored label.")]
public record BadgeComponent(
    [property: JsonPropertyName("text")]
    [property: Description("The text of the label.")]
    string Text,

    [property: JsonPropertyName("variant")]
    [property: Description("The status conveyed: its color follows the variant.")]
    BadgeVariant Variant = BadgeVariant.Neutral) : CardComponent
{
    /// <summary>A badge counts this many characters beyond its text when the card is measured against a channel's length budget.</summary>
    private const int Overhead = 2;

    /// <inheritdoc />
    public override int EstimateCost() => Text.Length + Overhead;
}

/// <summary>
/// Image component for displaying visual content from URLs.
/// Supports captions, alt text and size variants.
/// </summary>
/// <param name="Src">Image URL (must be publicly accessible, HTTPS recommended)</param>
/// <param name="Alt">Alternative text for accessibility (optional but recommended)</param>
/// <param name="Caption">Optional caption displayed below the image</param>
/// <param name="Size">Display size variant (small, medium, large, or full)</param>
[Description("A picture shown from its URL.")]
public record ImageComponent(
    [property: JsonPropertyName("src")]
    [property: Description("The public URL of the picture.")]
    string Src,

    [property: JsonPropertyName("alt")]
    [property: Description("What the picture shows, for readers who cannot see it.")]
    string? Alt = null,

    [property: JsonPropertyName("caption")]
    [property: Description("A line displayed below the picture.")]
    string? Caption = null,

    [property: JsonPropertyName("size")]
    [property: Description("How large the picture is displayed.")]
    ImageSize Size = ImageSize.Medium) : CardComponent
{
    /// <summary>An image counts this many characters for the placeholder that stands for it when the card is measured against a channel's length budget.</summary>
    private const int PlaceholderOverhead = 10;

    /// <summary>The caption counts this many characters beyond its text when the card is measured against a channel's length budget.</summary>
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
[Description("How a text block is styled.")]
[JsonConverter(typeof(JsonStringEnumConverter<TextStyle>))]
public enum TextStyle
{
    /// <summary>Default body text.</summary>
    [JsonStringEnumMemberName("normal")] Normal,
    /// <summary>Bold emphasis.</summary>
    [JsonStringEnumMemberName("bold")] Bold,
    /// <summary>De-emphasized / secondary text.</summary>
    [JsonStringEnumMemberName("muted")] Muted,
    /// <summary>Reduced font size, typically for captions or footnotes.</summary>
    [JsonStringEnumMemberName("small")] Small
}

/// <summary>
/// List presentation styles for ListComponent.
/// </summary>
[Description("The markers shown before the items of a list.")]
[JsonConverter(typeof(JsonStringEnumConverter<ListStyle>))]
public enum ListStyle
{
    /// <summary>Unordered list with bullet markers.</summary>
    [JsonStringEnumMemberName("bullet")] Bullet,
    /// <summary>Ordered list with numeric markers.</summary>
    [JsonStringEnumMemberName("numbered")] Numbered,
    /// <summary>List with no markers.</summary>
    [JsonStringEnumMemberName("plain")] Plain
}

/// <summary>
/// Badge color variants for BadgeComponent.
/// </summary>
[Description("The status that a badge conveys, which sets its color.")]
[JsonConverter(typeof(JsonStringEnumConverter<BadgeVariant>))]
public enum BadgeVariant
{
    /// <summary>Positive outcome (green).</summary>
    [JsonStringEnumMemberName("success")] Success,
    /// <summary>Cautionary state (amber).</summary>
    [JsonStringEnumMemberName("warning")] Warning,
    /// <summary>Failure or blocking condition (red).</summary>
    [JsonStringEnumMemberName("error")] Error,
    /// <summary>Informational note (blue).</summary>
    [JsonStringEnumMemberName("info")] Info,
    /// <summary>Unstyled / default badge.</summary>
    [JsonStringEnumMemberName("neutral")] Neutral
}

/// <summary>
/// Size variants for image components.
/// Controls maximum width of displayed images with responsive behavior.
/// </summary>
[Description("How large a picture is displayed.")]
[JsonConverter(typeof(JsonStringEnumConverter<ImageSize>))]
public enum ImageSize
{
    /// <summary>200px max-width — thumbnails, icons, avatars.</summary>
    [JsonStringEnumMemberName("small")] Small,
    /// <summary>400px max-width — default for most images.</summary>
    [JsonStringEnumMemberName("medium")] Medium,
    /// <summary>600px max-width — detailed images or diagrams.</summary>
    [JsonStringEnumMemberName("large")] Large,
    /// <summary>100% container width — banners and wide images.</summary>
    [JsonStringEnumMemberName("full")] Full
}