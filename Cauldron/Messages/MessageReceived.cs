namespace Cauldron.Messages;

public record MessageReceived(
    string ConversationId,
    string Text,
    DateTime Timestamp,
    string? MessageType = null,
    List<QuickReply>? QuickReplies = null,
    string? ErrorReason = null
);