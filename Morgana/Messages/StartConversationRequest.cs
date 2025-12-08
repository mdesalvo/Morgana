namespace Morgana.Messages;

public record StartConversationRequest(string ConversationId, string UserId, string? InitialContext = null);