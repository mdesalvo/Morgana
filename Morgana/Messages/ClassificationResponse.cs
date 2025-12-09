using System.Text.Json.Serialization;

namespace Morgana.Messages;

public record ClassificationResponse(
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("intent")] string Intent,
    [property: JsonPropertyName("confidence")] double Confidence);