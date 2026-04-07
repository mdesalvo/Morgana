using Cauldron.Messages;

namespace Cauldron.Interfaces;

/// <summary>
/// Service for managing the chat UI state: message list, temporary messages,
/// agent tracking, sending state, and UI state queries.
/// </summary>
public interface IChatStateService
{
    /// <summary>
    /// All chat messages in the current conversation.
    /// </summary>
    List<ChatMessage> ChatMessages { get; }

    /// <summary>
    /// Active temporary system warnings (rate limit errors, etc.).
    /// </summary>
    List<SignalRMessage> TemporaryMessages { get; }

    /// <summary>
    /// Unique identifier for the current conversation.
    /// </summary>
    string ConversationId { get; set; }

    /// <summary>
    /// Name of the currently active agent ("Morgana", "Morgana (Billing)", etc.).
    /// </summary>
    string CurrentAgentName { get; set; }

    /// <summary>
    /// True when connected to SignalR and ready to receive messages.
    /// </summary>
    bool IsConnected { get; set; }

    /// <summary>
    /// True during HTTP POST to prevent duplicate sends.
    /// </summary>
    bool IsSending { get; set; }

    /// <summary>
    /// True after SignalR connection and conversation start succeed.
    /// </summary>
    bool IsInitialized { get; set; }

    /// <summary>
    /// True after storage has been checked for existing conversation.
    /// </summary>
    bool HasCheckedStorage { get; set; }

    /// <summary>
    /// Adds a user message to the chat.
    /// </summary>
    void AddUserMessage(string text);

    /// <summary>
    /// Adds a typing indicator for the current agent.
    /// </summary>
    void AddTypingIndicator();

    /// <summary>
    /// Removes the last typing indicator from the message list.
    /// </summary>
    /// <returns>True if a typing indicator was found and removed.</returns>
    bool RemoveTypingIndicator();

    /// <summary>
    /// Adds a chat error message to both the temporary banner list and the permanent chat history.
    /// </summary>
    void AddChatError(string text, string errorReason, int? fadingDurationSeconds = 10);

    /// <summary>
    /// Adds a temporary error banner (no permanent chat message).
    /// </summary>
    void AddErrorBanner(string text, string errorReason, int? fadingDurationSeconds = 10);

    /// <summary>
    /// Removes all error-type temporary messages.
    /// </summary>
    void ClearErrorMessages();

    /// <summary>
    /// Dismisses a specific temporary message.
    /// </summary>
    void DismissTemporaryMessage(SignalRMessage message);

    /// <summary>
    /// Gets the completion message when an agent finishes its task.
    /// </summary>
    string GetCompletionMessage(string agentName);

    /// <summary>
    /// Updates the current agent name based on a received SignalR message.
    /// </summary>
    /// <returns>True if agent name was actually changed.</returns>
    bool UpdateAgentFromMessage(SignalRMessage message);

    /// <summary>
    /// Adds a completion presentation message if the agent just finished.
    /// </summary>
    void AddCompletionMessageIfNeeded(SignalRMessage message);

    /// <summary>
    /// Checks if an agent name represents a specialized agent (not base Morgana).
    /// Specialized agents have names with parentheses, e.g., "Morgana (Billing)".
    /// </summary>
    bool IsSpecializedAgent(string? agentName);

    /// <summary>
    /// Returns the CSS class string for the agent type: "agent" for specialized, "morgana" for base.
    /// </summary>
    string GetAgentClass(string? agentName);

    /// <summary>
    /// Gets the CSS color variable for the avatar border based on agent type.
    /// </summary>
    string GetAvatarBorderColor(string? agentName);

    /// <summary>
    /// True if any message has unselected quick replies from a history restore.
    /// </summary>
    bool HasActiveQuickReplies();

    /// <summary>
    /// True if any message has a rich card from a history restore.
    /// </summary>
    bool HasActiveRichCard();

    /// <summary>
    /// True if any message is currently showing a typing indicator.
    /// </summary>
    bool HasTypingIndicator();

    /// <summary>
    /// Resets all state for a fresh start.
    /// </summary>
    void Reset();
}