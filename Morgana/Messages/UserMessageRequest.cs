namespace Morgana.Messages;

public record UserMessageRequest(string UserId, string SessionId, string Message);