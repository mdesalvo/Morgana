namespace Morgana.Messages;

public record SendMessageRequest(
    string ConversationId,
    string UserId,
    string Text,
    Dictionary<string, object>? Metadata = null
);