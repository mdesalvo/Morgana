namespace Morgana.Messages;

public record StartConversationRequest(string UserId, string? InitialContext = null);