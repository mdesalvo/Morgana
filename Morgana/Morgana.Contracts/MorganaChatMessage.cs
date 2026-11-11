namespace Morgana.Contracts;

/// <summary>A single message of a conversation history as it travels over the wire to a channel.</summary>
// The persistence layer maps it from Microsoft.Agents.AI.ChatMessage.
public record MorganaChatMessage
{
    /// <summary>The conversation this message belongs to.</summary>
    public required string ConversationId { get; init; }

    /// <summary>The message text displayed to the user, extracted from the TextContent blocks of ChatMessage.Content.</summary>
    public required string Text { get; init; }

    /// <summary>When the message was created or received, as mapped from ChatMessage.CreatedAt.</summary>
    public required DateTime Timestamp { get; init; }

    /// <summary>Whether the message came from the user or from an assistant, derived from ChatMessage.Role.</summary>
    public required ChatMessageType Type { get; init; }

    /// <summary>The message role for CSS styling, either "user" or "assistant".</summary>
    public string Role => Type switch
    {
        // A message from the user styles as the user's own.
        ChatMessageType.User => "user",
        // Every other type is rendered as an answer.
        _ => "assistant"
    };

    /// <summary>The name of the agent that generated this message, "User" for the user's own.</summary>
    public required string AgentName { get; init; }

    /// <summary>True when the agent has completed its task, as mapped from the SQLite is_active column.</summary>
    // Inverted from storage: true when is_active = 0 and false when is_active = 1.
    public required bool AgentCompleted { get; init; }

    /// <summary>The quick reply buttons attached to this message, reconstructed from SetQuickReplies tool calls when history loads.</summary>
    public List<QuickReply>? QuickReplies { get; init; }

    /// <summary>True when this is the last message of a resumed conversation.</summary>
    public bool? IsLastHistoryMessage { get; init; }

    /// <summary>The rich card attached to this message, reconstructed from SetRichCard tool calls when history loads.</summary>
    public RichCard? RichCard { get; init; }
}

/// <summary>Whether a history message came from the user or from an agent.</summary>
/// <remarks>Serialized numerically by the default web JSON options, so the declaration order is part of the wire contract and inserting a value in the middle silently re-numbers every value after it.</remarks>
// New values are appended at the end.
public enum ChatMessageType
{
    /// <summary>Message from the user.</summary>
    User,

    /// <summary>Regular response from an agent.</summary>
    Assistant
}