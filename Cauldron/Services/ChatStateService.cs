using Cauldron.Interfaces;
using Cauldron.Messages;

namespace Cauldron.Services;

/// <summary>
/// Manages the chat UI state: message list, temporary messages, agent tracking, and sending state.
/// </summary>
public class ChatStateService : IChatStateService
{
    private readonly IConfiguration _configuration;

    public ChatStateService(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    // =========================================================================
    // STATE
    // =========================================================================

    /// <summary>
    /// All chat messages in the current conversation.
    /// </summary>
    public List<ChatMessage> ChatMessages { get; } = [];

    /// <summary>
    /// Active temporary system warnings (rate limit errors, etc.).
    /// </summary>
    public List<ChannelMessage> TemporaryMessages { get; } = [];

    /// <summary>
    /// Unique identifier for the current conversation.
    /// </summary>
    public string ConversationId { get; set; } = string.Empty;

    /// <summary>
    /// Name of the currently active agent ("Morgana", "Morgana (Billing)", etc.).
    /// </summary>
    public string CurrentAgentName { get; set; } = "Morgana";

    /// <summary>
    /// True when connected to SignalR and ready to receive messages.
    /// </summary>
    public bool IsConnected { get; set; }

    /// <summary>
    /// True during HTTP POST to prevent duplicate sends.
    /// </summary>
    public bool IsSending { get; set; }

    /// <summary>
    /// True after SignalR connection and conversation start succeed.
    /// </summary>
    public bool IsInitialized { get; set; }

    /// <summary>
    /// True after storage has been checked for existing conversation.
    /// </summary>
    public bool HasCheckedStorage { get; set; }

    // =========================================================================
    // MESSAGE OPERATIONS
    // =========================================================================

    /// <summary>
    /// Adds a user message to the chat.
    /// </summary>
    public void AddUserMessage(string text)
    {
        ChatMessages.Add(new ChatMessage
        {
            ConversationId = ConversationId,
            Text = text,
            Role = "user",
            Timestamp = DateTime.UtcNow
        });
    }

    /// <summary>
    /// Adds a typing indicator for the current agent.
    /// </summary>
    public void AddTypingIndicator()
    {
        ChatMessages.Add(new ChatMessage
        {
            ConversationId = ConversationId,
            Role = "assistant",
            IsTyping = true,
            AgentName = CurrentAgentName,
            Timestamp = DateTime.UtcNow
        });
    }

    /// <summary>
    /// Removes the last typing indicator from the message list.
    /// </summary>
    /// <returns>True if a typing indicator was found and removed.</returns>
    public bool RemoveTypingIndicator()
    {
        ChatMessage? typingMessage = ChatMessages.LastOrDefault(m => m.IsTyping);
        if (typingMessage != null)
        {
            ChatMessages.Remove(typingMessage);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Adds a chat error message to both the temporary banner list and the permanent chat history.
    /// </summary>
    public void AddChatError(string text, string errorReason, int? fadingDurationSeconds = 10)
    {
        // Temporary banner
        TemporaryMessages.Add(new ChannelMessage
        {
            ConversationId = ConversationId,
            Text = text,
            Timestamp = DateTime.UtcNow,
            MessageType = "error",
            ErrorReason = errorReason,
            FadingMessageDurationSeconds = fadingDurationSeconds
        });

        // Permanent message in chat
        ChatMessages.Add(new ChatMessage
        {
            ConversationId = ConversationId,
            Text = "Sorry, an error occurred. Please try again.",
            Role = "assistant",
            Timestamp = DateTime.UtcNow,
            IsError = true,
            AgentName = CurrentAgentName,
            Type = MessageType.Error
        });
    }

    /// <summary>
    /// Adds a temporary error banner (no permanent chat message).
    /// </summary>
    public void AddErrorBanner(string text, string errorReason, int? fadingDurationSeconds = 10)
    {
        TemporaryMessages.Add(new ChannelMessage
        {
            ConversationId = ConversationId,
            Text = text,
            Timestamp = DateTime.UtcNow,
            MessageType = "error",
            ErrorReason = errorReason,
            FadingMessageDurationSeconds = fadingDurationSeconds
        });
    }

    /// <summary>
    /// Removes all error-type temporary messages.
    /// </summary>
    public void ClearErrorMessages()
    {
        TemporaryMessages.RemoveAll(m =>
            string.Equals(m.MessageType, "error", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Dismisses a specific temporary message.
    /// </summary>
    public void DismissTemporaryMessage(ChannelMessage message)
    {
        TemporaryMessages.Remove(message);
    }

    // =========================================================================
    // AGENT HELPERS
    // =========================================================================

    /// <summary>
    /// Checks if an agent name represents a specialized agent (not base Morgana).
    /// </summary>
    public bool IsSpecializedAgent(string? agentName)
    {
        return agentName != null && agentName.Contains('(') && agentName.Contains(')');
    }

    /// <summary>
    /// Returns the CSS class string for the agent type.
    /// </summary>
    public string GetAgentClass(string? agentName) =>
        IsSpecializedAgent(agentName) ? "agent" : "morgana";

    /// <summary>
    /// Gets the CSS color variable for the avatar border based on agent type.
    /// </summary>
    public string GetAvatarBorderColor(string? agentName) =>
        IsSpecializedAgent(agentName ?? "Morgana") ? "var(--secondary-color)" : "var(--primary-color)";

    /// <summary>
    /// Gets the completion message when an agent finishes its task.
    /// </summary>
    public string GetCompletionMessage(string agentName)
    {
        string template = _configuration["Cauldron:AgentExitMessage"]
                          ?? "{0} has completed its spell. I'm back to you!";
        return string.Format(template, agentName);
    }

    /// <summary>
    /// Updates the current agent name based on a received SignalR message.
    /// </summary>
    /// <returns>True if agent name was actually changed.</returns>
    public bool UpdateAgentFromMessage(ChannelMessage message)
    {
        string newAgentName = message.AgentCompleted || string.IsNullOrEmpty(message.AgentName)
            ? "Morgana"
            : message.AgentName;

        if (!string.Equals(CurrentAgentName, newAgentName, StringComparison.OrdinalIgnoreCase))
        {
            CurrentAgentName = newAgentName;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Adds a completion presentation message if the agent just finished.
    /// </summary>
    public void AddCompletionMessageIfNeeded(ChannelMessage message)
    {
        if (message.AgentCompleted && IsSpecializedAgent(message.AgentName))
        {
            ChatMessages.Add(new ChatMessage
            {
                ConversationId = message.ConversationId,
                Text = GetCompletionMessage(message.AgentName),
                Role = "assistant",
                Timestamp = message.Timestamp.AddMilliseconds(5),
                AgentName = "Morgana",
                AgentCompleted = true,
                Type = MessageType.Presentation
            });
        }
    }

    // =========================================================================
    // UI STATE QUERIES
    // =========================================================================

    /// <summary>
    /// True if any message has unselected quick replies from a history restore.
    /// </summary>
    public bool HasActiveQuickReplies() =>
        ChatMessages.Any(m => m is { QuickReplies: not null, SelectedQuickReplyId: null, IsLastHistoryMessage: true });

    /// <summary>
    /// True if any message has a rich card from a history restore.
    /// </summary>
    public bool HasActiveRichCard() =>
        ChatMessages.Any(m => m is { RichCard: not null, IsLastHistoryMessage: true });

    /// <summary>
    /// True if any message is currently showing a typing indicator.
    /// </summary>
    public bool HasTypingIndicator() =>
        ChatMessages.Any(m => m.IsTyping);

    /// <summary>
    /// Resets all state for a fresh start.
    /// </summary>
    public void Reset()
    {
        ChatMessages.Clear();
        TemporaryMessages.Clear();
        ConversationId = string.Empty;
        CurrentAgentName = "Morgana";
        IsConnected = false;
        IsSending = false;
        IsInitialized = false;
        HasCheckedStorage = false;
    }
}
