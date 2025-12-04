namespace Morgana.Messages;

public record ArchiveRequest(string UserId, string SessionId, string UserMessage, string BotResponse, ClassificationResult Classification);