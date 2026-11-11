using System.Text.Json.Serialization;

namespace Morgana.Contracts;

/// <summary>Transport-agnostic envelope for a structured outbound message delivered to the end user through an <c>IChannelService</c>.</summary>
public sealed record ChannelMessage
{
    /// <summary>
    /// The conversation this message belongs to.
    /// Doubles as the key for channel group routing.
    /// </summary>
    [JsonPropertyName("conversationId")]
    public required string ConversationId { get; init; }

    /// <summary>
    /// The message text shown to the user.
    /// Whether it may carry markdown depends on the target channel's ChannelCapabilities.SupportsMarkdown.
    /// </summary>
    [JsonPropertyName("text")]
    public required string Text { get; init; }

    /// <summary>The UTC time at which the backend generated this message.</summary>
    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    /// <summary>The kind of message as one of <see cref="ChannelMessageTypes"/>, an answer by default.</summary>
    [JsonPropertyName("messageType")]
    public string MessageType { get; init; } = ChannelMessageTypes.Assistant;

    /// <summary>
    /// The quick reply buttons to render as clickable choices; null when there are none.
    /// The model generates quick replies and they constitute a stable contract.
    /// </summary>
    [JsonPropertyName("quickReplies")]
    public List<QuickReply>? QuickReplies { get; init; }

    /// <summary>The rich card that presents structured data in place of plain text formatting; null when there is none.</summary>
    [JsonPropertyName("richCard")]
    public RichCard? RichCard { get; init; }

    /// <summary>
    /// Why a notice was sent, one of <see cref="ChannelErrorReasons"/>, when MessageType is <see cref="ChannelMessageTypes.Error"/> or <see cref="ChannelMessageTypes.SystemWarning"/>; null otherwise.
    /// A failed delivery reports its cause after the prefix delivery_error:.
    /// </summary>
    [JsonPropertyName("errorReason")]
    public string? ErrorReason { get; init; }

    /// <summary>The name of the agent that generated this message, "Morgana" by default.</summary>
    [JsonPropertyName("agentName")]
    public string AgentName { get; init; } = "Morgana";

    /// <summary>True when the specialized agent has completed its task and control returns to the base Morgana orchestrator.</summary>
    [JsonPropertyName("agentCompleted")]
    public bool AgentCompleted { get; init; }

    /// <summary>The seconds that the fading notice generated from this message stays visible, depending on its MessageType.</summary>
    [JsonPropertyName("fadingMessageDurationSeconds")]
    public int? FadingMessageDurationSeconds { get; init; } = 10;

    /// <summary>
    /// How far the command that sent this message has got; present only on a frame that a running command pushes.
    /// A frame reports work rather than saying something: a channel drawing it keeps it out of the transcript
    /// and lets the next frame replace it. Null on everything said in the conversation.
    /// </summary>
    [JsonPropertyName("progress")]
    public CommandProgress? Progress { get; init; }

    /// <summary>Conversation-level metadata such as the remaining dust budget; null when none is available.</summary>
    [JsonPropertyName("conversationMetadata")]
    public ConversationMetadata? ConversationMetadata { get; init; }
}