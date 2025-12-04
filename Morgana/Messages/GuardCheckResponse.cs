namespace Morgana.Messages;

public record GuardCheckResponse(bool IsCompliant, string? Violation);