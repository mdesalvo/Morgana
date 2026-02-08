using System.Text.Json.Serialization;

namespace Cauldron.Messages;

/// <summary>
/// Rich card container for structured visual presentation.
/// Mirrors Morgana.Framework.Records.RichCard structure.
/// </summary>
public class RichCard
{
    public required string Title { get; set; }
    public string? Subtitle { get; set; }
    public required List<CardComponent> Components { get; set; }
}

/// <summary>
/// Base class for all card components.
/// Uses System.Text.Json polymorphic deserialization.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TextBlockComponent), "text_block")]
[JsonDerivedType(typeof(KeyValueComponent), "key_value")]
[JsonDerivedType(typeof(DividerComponent), "divider")]
[JsonDerivedType(typeof(ListComponent), "list")]
[JsonDerivedType(typeof(SectionComponent), "section")]
[JsonDerivedType(typeof(GridComponent), "grid")]
[JsonDerivedType(typeof(BadgeComponent), "badge")]
[JsonDerivedType(typeof(ImageComponent), "image")]
public abstract class CardComponent;

public class TextBlockComponent : CardComponent
{
    public required string Content { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter<TextStyle>))]
    public TextStyle Style { get; set; } = TextStyle.Normal;
}

public class KeyValueComponent : CardComponent
{
    public required string Key { get; set; }
    public required string Value { get; set; }
    public bool Emphasize { get; set; }
}

public class DividerComponent : CardComponent;

public class ListComponent : CardComponent
{
    public required List<string> Items { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter<ListStyle>))]
    public ListStyle Style { get; set; } = ListStyle.Bullet;
}

public class SectionComponent : CardComponent
{
    public required string Title { get; set; }
    public string? Subtitle { get; set; }
    public required List<CardComponent> Components { get; set; }
}

public class GridComponent : CardComponent
{
    public int Columns { get; set; }
    public required List<GridItem> Items { get; set; }
}

public class GridItem
{
    public required string Key { get; set; }
    public required string Value { get; set; }
}

public class BadgeComponent : CardComponent
{
    public required string Text { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter<BadgeVariant>))]
    public BadgeVariant Variant { get; set; } = BadgeVariant.Neutral;
}

public class ImageComponent : CardComponent
{
    public required string Src { get; set; }
    public string? Alt { get; set; }
    public string? Caption { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter<ImageSize>))]
    public ImageSize Size { get; set; } = ImageSize.Medium;
}

// Enums

public enum TextStyle
{
    Normal,
    Bold,
    Muted,
    Small
}

public enum ListStyle
{
    Bullet,
    Numbered,
    Plain
}

public enum BadgeVariant
{
    Success,
    Warning,
    Error,
    Info,
    Neutral
}

public enum ImageSize
{
    Small,
    Medium,
    Large,
    Full
}