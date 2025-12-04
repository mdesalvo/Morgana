namespace Morgana.Messages;

public record UserMessage(string UserId, string SessionId, string Content);