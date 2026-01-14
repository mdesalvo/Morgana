namespace Cauldron.Messages;

/// <summary>
/// Response model from POST /api/conversation/start endpoint.
/// Contains the unique conversation identifier and initial status message.
/// </summary>
/// <remarks>
/// <para><strong>Usage Flow:</strong></para>
/// <list type="number">
/// <item>Client calls POST /api/conversation/start with new GUID</item>
/// <item>Backend creates conversation, supervisor, and initiates presentation</item>
/// <item>Backend returns ConversationStartResponse with conversationId</item>
/// <item>Client joins SignalR group using conversationId</item>
/// <item>Backend sends presentation message via SignalR</item>
/// </list>
/// <para><strong>Example Response:</strong></para>
/// <code>
/// {
///   "conversationId": "abc123def456",
///   "message": "Conversation started successfully"
/// }
/// </code>
/// </remarks>
public class ConversationStartResponse
{
    /// <summary>
    /// Unique identifier for the newly created conversation.
    /// Used for all subsequent message API calls and SignalR group membership.
    /// </summary>
    /// <remarks>
    /// This ID is typically a GUID in compact format (32 characters, no hyphens).
    /// Example: "a1b2c3d4e5f6g7h8i9j0k1l2m3n4o5p6"
    /// </remarks>
    public string ConversationId { get; set; } = string.Empty;

    /// <summary>
    /// Status message indicating successful conversation creation.
    /// Typically: "Conversation started successfully" or similar confirmation.
    /// </summary>
    /// <remarks>
    /// This message is for debugging/logging purposes and not typically displayed to end users.
    /// The actual welcome message arrives separately via SignalR as a presentation message.
    /// </remarks>
    public string Message { get; set; } = string.Empty;
}