namespace Morgana.Messages;

public record ExecuteRequest(string UserId, string SessionId, string Content, ClassificationResult Classification);