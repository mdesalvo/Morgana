namespace Morgana.Contracts;

/// <summary>
/// A single message of a conversation history as it travels over the wire to a channel,
/// mapped from Microsoft.Agents.AI.ChatMessage by the persistence layer.
/// </summary>
public record MorganaChatMessage
{
    /// <summary>
    /// Unique identifier of the conversation this message belongs to.
    /// </summary>
    public required string ConversationId { get; init; }

    /// <summary>
    /// Message text content displayed to the user.
    /// Extracted from TextContent blocks in ChatMessage.Content.
    /// </summary>
    public required string Text { get; init; }

    /// <summary>
    /// Timestamp when the message was created or received.
    /// Mapped from ChatMessage.CreatedAt.
    /// </summary>
    public required DateTime Timestamp { get; init; }

    /// <summary>
    /// Type of message determining styling and behavior.
    /// Derived from ChatMessage.Role (user/assistant).
    /// </summary>
    public required ChatMessageType Type { get; init; }

    /// <summary>
    /// Gets the message role for CSS styling ("user" or "assistant").
    /// </summary>
    public string Role => Type switch
    {
        ChatMessageType.User => "user",
        _ => "assistant"
    };

    /// <summary>
    /// Name of the agent that generated this message.
    /// Examples: "User", "Morgana (billing)", "Morgana (contract)", ...
    /// </summary>
    public required string AgentName { get; init; }

    /// <summary>
    /// Indicates whether the agent has completed its task.
    /// Mapped from SQLite is_active column: true when is_active = 0, false when is_active = 1.
    /// </summary>
    public required bool AgentCompleted { get; init; }

    /// <summary>
    /// Optional list of quick reply buttons attached to this message.
    /// Reconstructed from SetQuickReplies tool calls when loading conversation history.
    /// </summary>
    public List<QuickReply>? QuickReplies { get; init; }

    /// <summary>
    /// Optional flag indicating that this is the last message of a resumed conversation.
    /// </summary>
    public bool? IsLastHistoryMessage { get; init; }

    /// <summary>
    /// Optional rich card attached to this message.
    /// Reconstructed from SetRichCard tool calls when loading conversation history.
    /// </summary>
    public RichCard? RichCard { get; init; }
}

/// <summary>
/// Enumeration of message types for styling and behavior differentiation.
/// </summary>
/// <remarks>
/// Serialized numerically by the default web JSON options, so the declaration order is part of
/// the wire contract: inserting a value in the middle silently re-numbers every value after it.
/// Append new values at the end.
/// </remarks>
public enum ChatMessageType
{
    /// <summary>
    /// Message from the user.
    /// Displayed on the right side with user styling.
    /// </summary>
    User,

    /// <summary>
    /// Regular response from an agent.
    /// Displayed on the left side with agent avatar and assistant styling.
    /// </summary>
    Assistant
}