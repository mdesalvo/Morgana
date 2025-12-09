namespace Morgana.Messages;

public record UserMessage(
    string ConversationId,
    string UserId,
    string Text,
    DateTime Timestamp
);