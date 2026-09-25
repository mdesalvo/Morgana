namespace Morgana.Contracts;

/// <summary>
/// HTTP request model for sending a message to a conversation via REST API
/// (<c>POST /api/morgana/conversation/{id}/message</c>): the route names the conversation, the body only what is said to it.
/// </summary>
/// <param name="Text">Message text from the user</param>
/// <param name="Metadata">Optional metadata dictionary</param>
public record SendMessageRequest(
    string Text,
    Dictionary<string, object>? Metadata = null
);