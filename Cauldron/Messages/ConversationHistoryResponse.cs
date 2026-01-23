namespace Cauldron.Messages;

/// <summary>
/// Response model from GET /api/conversation/{id}/history endpoint.
/// Contains the complete conversation history for UI rendering.
/// </summary>
/// <remarks>
/// <para><strong>Usage Flow:</strong></para>
/// <list type="number">
/// <item>Client resumes conversation (POST /api/conversation/{id}/resume)</item>
/// <item>Client joins SignalR group (await SignalRService.JoinConversation)</item>
/// <item>Client calls GET /api/conversation/{id}/history</item>
/// <item>Backend returns ConversationHistoryResponse with messages array</item>
/// <item>Client maps MorganaChatMessage[] to ChatMessage[] and populates UI</item>
/// </list>
/// <para><strong>Example Response:</strong></para>
/// <code>
/// {
///   "messages": [
///     {
///       "conversationId": "abc123",
///       "messageText": "Hello, I need help with my invoice",
///       "role": "user",
///       "agentName": "User",
///       "agentCompleted": false,
///       "createdAt": "2025-01-22T10:00:00Z"
///     },
///     {
///       "conversationId": "abc123",
///       "messageText": "Sure, I can help you with that. Can you provide your invoice ID?",
///       "role": "assistant",
///       "agentName": "billing",
///       "agentCompleted": false,
///       "createdAt": "2025-01-22T10:00:05Z"
///     }
///   ]
/// }
/// </code>
/// </remarks>
public class ConversationHistoryResponse
{
    /// <summary>
    /// Array of messages from the conversation history, chronologically ordered.
    /// Maps directly from MorganaChatMessage[] returned by backend persistence service.
    /// </summary>
    public ChatMessage[] Messages { get; set; } = [];
}