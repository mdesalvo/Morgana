using System.Text.Json.Serialization;

namespace Morgana.Messages;

public record GuardCheckResponse(
    [property: JsonPropertyName("compliant")] bool Compliant,
    [property: JsonPropertyName("violation")] string? Violation);