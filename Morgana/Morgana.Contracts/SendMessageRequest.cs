namespace Morgana.Contracts;

/// <summary>
/// HTTP request model for sending a message to a conversation via REST API.
/// </summary>
/// <param name="ConversationId">Unique identifier of the target conversation</param>
/// <param name="Text">Message text from the user</param>
/// <param name="Metadata">Optional metadata dictionary (reserved for future use)</param>
public record SendMessageRequest(
    string ConversationId,
    string Text,
    Dictionary<string, object>? Metadata = null
);