namespace Morgana.Messages;

public record SendMessageRequest(
    string UserId,
    string Text,
    Dictionary<string, object>? Metadata = null
);