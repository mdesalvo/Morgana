namespace Morgana.Messages;

public record ConversationResponse(string Response, string Classification, Dictionary<string, string> Metadata);