using Cauldron.Interfaces;
using Cauldron.Messages;
using Morgana.Contracts;

namespace Cauldron.Services;

/// <summary>
/// Holds every piece of UI state for one chat session: the message list, the transient banners,
/// the active agent and the flags that gate the input. Scoped, so one instance per Blazor circuit.
/// </summary>
public class ChatStateService : IChatStateService
{
    /// <summary>
    /// Name of the base agent: the one that speaks when no specialist is engaged.
    /// </summary>
    public const string BaseAgentName = "Morgana";

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
    public string CurrentAgentName { get; set; } = BaseAgentName;

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

    /// <summary>
    /// REMAINING dust as a fraction of the conversation's budget (1.0 = full, 0.0 =
    /// empty), as last reported by Morgana. Null when dust limiting is disabled — gauge hidden.
    /// </summary>
    public double? DustLevel { get; set; }

    /// <inheritdoc />
    public DateTime LatestDeliveredTimestamp { get; private set; } = DateTime.MinValue;

    /// <inheritdoc />
    public void NoteDelivered(DateTime morganaTimestamp)
    {
        if (morganaTimestamp > LatestDeliveredTimestamp)
            LatestDeliveredTimestamp = morganaTimestamp;
    }

    // =========================================================================
    // MESSAGE OPERATIONS
    // =========================================================================

    /// <inheritdoc />
    public string ComposerText { get; set; } = string.Empty;

    /// <summary>
    /// What a turn put on the screen and the words it took from the composer, kept so that the turn can be undone.
    /// </summary>
    private sealed record OpenTurn(ChatMessage UserMessage, ChatMessage? AnsweredOffering, string WordsToGiveBack);

    /// <summary>
    /// The turn that <see cref="BeginTurn"/> opened and nothing has closed yet, null while none is open.
    /// </summary>
    private OpenTurn? openTurn;

    /// <inheritdoc />
    public void BeginTurn(string text)
    {
        // Only the latest assistant message can hold the buttons on offer: an older row never is the one this turn answers
        ChatMessage? latestAssistantMessage = ChatMessages.LastOrDefault(message => message.Type == MessageType.Assistant && !message.IsTyping);
        ChatMessage? answeredOffering = latestAssistantMessage is { QuickReplies.Count: > 0 } ? latestAssistantMessage : null;

        // A press has already marked its button on the offering and its value is not the user's words: only a typed turn has words to give back
        string wordsToGiveBack = answeredOffering is { SelectedQuickReplyId: not null } ? string.Empty : text;

        // One turn at a time: the composer and the buttons stay shut until the answer arrives or the turn is withdrawn
        IsSending = true;

        // On offer are the unanswered buttons of a live message or of the last message of a resumed history:
        // left enabled they would send a press that answers a turn the user has already moved past.
        foreach (ChatMessage offeringMessage in ChatMessages.Where(message => message is { QuickReplies.Count: > 0, SelectedQuickReplyId: null, QuickRepliesRetired: false, IsLastHistoryMessage: not false }))
            offeringMessage.QuickRepliesRetired = true;

        // Echoed locally and immediately: the user sees their own words without waiting
        // for the server, which never sends them back.
        ChatMessage userMessage = new ChatMessage
        {
            ConversationId = ConversationId,
            Text = text,
            Type = MessageType.User,
            Timestamp = DateTime.UtcNow
        };
        ChatMessages.Add(userMessage);

        // Shown immediately: the real answer arrives over SignalR, so without this the UI
        // would sit inert for the whole time the agent is thinking.
        AddTypingIndicator();

        // What a refusal of this turn would have to undo, until the next turn replaces it
        openTurn = new OpenTurn(userMessage, answeredOffering, wordsToGiveBack);
    }

    /// <inheritdoc />
    public void WithdrawTurn()
    {
        // No turn is open, so there is nothing to undo
        if (openTurn == null)
            return;

        // The user's bubble and the indicator go away: Morgana recorded neither
        ChatMessages.Remove(openTurn.UserMessage);
        RemoveTypingIndicator();

        // The buttons that the turn answered are the offer again, with no selection
        if (openTurn.AnsweredOffering != null)
        {
            openTurn.AnsweredOffering.SelectedQuickReplyId = null;
            openTurn.AnsweredOffering.QuickRepliesRetired = false;
        }

        // Typed words return to the composer so the user does not retype them
        ComposerText = openTurn.WordsToGiveBack;
        IsSending = false;
        openTurn = null;
    }

    /// <summary>
    /// Adds a typing indicator for the current agent.
    /// </summary>
    public void AddTypingIndicator()
    {
        // Lives in the message list rather than beside it, so it occupies the spot the real
        // answer will take and the conversation does not jump when it is swapped out.
        ChatMessages.Add(new ChatMessage
        {
            ConversationId = ConversationId,
            Type = MessageType.Assistant,
            IsTyping = true,
            AgentName = CurrentAgentName,
            Timestamp = DateTime.UtcNow
        });
    }

    /// <summary>
    /// Removes the last typing indicator from the message list, if there is one.
    /// </summary>
    public void RemoveTypingIndicator()
    {
        // Last, not first: only the most recent turn can legitimately still be waiting
        ChatMessage? typingMessage = ChatMessages.LastOrDefault(m => m.IsTyping);

        // No indicator means the turn already got its first chunk or its reply
        if (typingMessage != null)
            ChatMessages.Remove(typingMessage);
    }

    /// <summary>
    /// Adds a chat error message to both the temporary banner list and the permanent chat history.
    /// </summary>
    public void AddChatError(string text, string errorReason, int? fadingDurationSeconds = null)
    {
        // The banner carries the technical detail and fades on its own
        AddErrorBanner(text, errorReason, fadingDurationSeconds);

        // The chat line stays forever and is deliberately vague: it marks where the
        // conversation broke without leaving a status code in the transcript.
        ChatMessages.Add(new ChatMessage
        {
            ConversationId = ConversationId,
            Text = "Sorry, an error occurred. Please try again.",
            Timestamp = DateTime.UtcNow,
            IsError = true,
            AgentName = CurrentAgentName,
            Type = MessageType.Error
        });
    }

    /// <summary>
    /// Adds a temporary error banner (no permanent chat message).
    /// </summary>
    public void AddErrorBanner(string text, string errorReason, int? fadingDurationSeconds = null)
    {
        // Stamped here with this conversation: the banner is born on this client, not received
        TemporaryMessages.Add(new ChannelMessage
        {
            ConversationId = ConversationId,
            Text = text,
            Timestamp = DateTime.UtcNow,
            MessageType = ChannelMessageTypes.Error,
            ErrorReason = errorReason,
            FadingMessageDurationSeconds = fadingDurationSeconds
        });
    }

    /// <summary>
    /// Shows a banner that Morgana sent, keeping at most one terminal dust-exhaustion notice on screen.
    /// </summary>
    public void AddTemporaryMessage(ChannelMessage message)
    {
        // The dust-exhausted notice is terminal and never fades, so further blocked sends must
        // not stack duplicate persistent banners. Transient notices (rate limit) fade on their own.
        bool isDustExhausted = string.Equals(
            message.ErrorReason, ChannelErrorReasons.DustBudgetExhausted, StringComparison.Ordinal);

        // The terminal banner is already showing: a second one would say nothing new
        if (isDustExhausted && IsConversationDeadFromDust())
            return;

        // Shown as it arrived: Morgana already set its text and fade duration
        TemporaryMessages.Add(message);
    }

    /// <summary>
    /// Removes all transient error-type temporary messages, but preserves the terminal
    /// dust-exhaustion banner: that is not a retryable error, it is a permanent
    /// "this conversation is dead" state marker. Clearing it (e.g. the post-init
    /// ClearErrorMessages on resume) would strand the user with a locked input and no
    /// explanation, so it survives until a brand-new conversation replaces all state.
    /// </summary>
    public void ClearErrorMessages()
    {
        TemporaryMessages.RemoveAll(m =>
            string.Equals(m.MessageType, ChannelMessageTypes.Error, StringComparison.OrdinalIgnoreCase)
             && !string.Equals(m.ErrorReason, ChannelErrorReasons.DustBudgetExhausted, StringComparison.Ordinal));
    }

    /// <summary>
    /// Dismisses a specific temporary message.
    /// </summary>
    public void DismissTemporaryMessage(ChannelMessage message)
    {
        // The banner faded or was closed by the user: it leaves the list so it stops rendering
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
        // Specialized agents are named "Morgana (Billing)"; the base one is plain "Morgana",
        // so the parentheses are the whole test.
        return agentName != null && agentName.Contains('(') && agentName.Contains(')');
    }

    /// <summary>
    /// Returns the CSS class string for the agent type.
    /// </summary>
    public string GetAgentClass(string? agentName) =>
        IsSpecializedAgent(agentName) ? "agent" : "morgana";

    /// <summary>
    /// Updates the current agent name based on a received SignalR message.
    /// </summary>
    public void UpdateAgentFromMessage(ChannelMessage message)
    {
        // A completed agent hands control back, so the header returns to base Morgana even
        // though the message itself was signed by the specialist that just finished.
        CurrentAgentName = message.AgentCompleted || string.IsNullOrEmpty(message.AgentName)
            ? BaseAgentName
            : message.AgentName;
    }

    /// <summary>
    /// Sets the current agent from the specialist that Morgana reports engaged on a resumed conversation.
    /// </summary>
    public void ResumeAgent(string? activeAgent)
    {
        // The base agent is stored as a row of its own: it is not a specialist to put in parentheses
        if (string.IsNullOrEmpty(activeAgent)
            || string.Equals(activeAgent, BaseAgentName, StringComparison.OrdinalIgnoreCase))
        {
            // No specialist engaged: Morgana herself
            CurrentAgentName = BaseAgentName;
        }
        else
        {
            // Specialists are stored lowercase while the header shows them capitalised
            CurrentAgentName = $"{BaseAgentName} ({char.ToUpperInvariant(activeAgent[0]) + activeAgent[1..]})";
        }
    }

    // =========================================================================
    // UI STATE QUERIES
    // =========================================================================

    /// <summary>
    /// True if any message is currently showing a typing indicator.
    /// </summary>
    public bool HasTypingIndicator() =>
        ChatMessages.Any(m => m.IsTyping);

    /// <summary>
    /// True when the conversation is terminally spent: a dust-exhaustion banner
    /// (ErrorReason <see cref="ChannelErrorReasons.DustBudgetExhausted"/>) is on screen. The conversation is
    /// dead and the only way forward is a brand-new one, so the "New Conversation"
    /// button must stay reachable even when the usual connection/history/init gates
    /// would otherwise hide it (e.g. a transient SignalR reconnect right after lockout).
    /// </summary>
    public bool IsConversationDeadFromDust() =>
        TemporaryMessages.Any(m =>
            string.Equals(m.ErrorReason, ChannelErrorReasons.DustBudgetExhausted, StringComparison.Ordinal));
}
