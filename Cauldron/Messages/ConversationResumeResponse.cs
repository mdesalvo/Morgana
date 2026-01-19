namespace Cauldron.Messages;

/// <summary>
/// Response model from POST /api/conversation/{id}/resume endpoint.
/// </summary>
public class ConversationResumeResponse
{
    /// <summary>
    /// Unique identifier of the resumed conversation.
    /// </summary>
    public string ConversationId { get; set; } = string.Empty;

    /// <summary>
    /// Indicates whether the conversation was successfully resumed.
    /// </summary>
    public bool Resumed { get; set; }

    /// <summary>
    /// Name of the active agent when conversation was persisted.
    /// </summary>
    public string ActiveAgent { get; set; } = string.Empty;
}