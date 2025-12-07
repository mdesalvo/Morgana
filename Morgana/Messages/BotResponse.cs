namespace Morgana.Messages;

public record BotResponse(
    string ConversationId, 
    string Text, 
    string? ErrorReason
);