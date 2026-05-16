namespace Cauldron.Messages;

/// <summary>
/// Response model from POST /api/morgana/conversation/{id}/resume endpoint.
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

    /// <summary>
    /// Consumed-dust ratio (0.0 to &gt;1.0) of the resumed conversation's lifetime
    /// budget, so the dust gauge can be rehydrated on resume rather than staying hidden
    /// until the first post-resume turn. Null when dust limiting is disabled on Morgana.
    /// </summary>
    public double? DustLevel { get; set; }
}