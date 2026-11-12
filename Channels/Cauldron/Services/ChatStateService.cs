using Cauldron.Interfaces;
using Cauldron.Messages;
using Morgana.Contracts;

namespace Cauldron.Services;

/// <summary>
/// Holds every piece of UI state of one chat session: the messages, the banners, the agent on screen and the flags that gate the input.
/// Scoped, so there is one instance per Blazor circuit.
/// </summary>
public class ChatStateService : IChatStateService
{
    /// <summary>The name base Morgana signs her messages with, which every specialist's name starts from.</summary>
    public const string BaseAgentName = "Morgana";

    /// <inheritdoc />
    public List<ChatMessage> ChatMessages { get; } = [];

    /// <inheritdoc />
    public List<ChannelMessage> TemporaryMessages { get; } = [];

    /// <inheritdoc />
    public string ConversationId { get; set; } = string.Empty;

    /// <inheritdoc />
    public string CurrentAgentName { get; set; } = BaseAgentName;

    /// <inheritdoc />
    public bool IsConnected { get; set; }

    /// <inheritdoc />
    public bool IsSending { get; set; }

    /// <inheritdoc />
    public bool IsInitialized { get; set; }

    /// <inheritdoc />
    public bool HasCheckedStorage { get; set; }

    /// <inheritdoc />
    public double? DustLevel { get; set; }

    /// <inheritdoc />
    public TimeSpan? ReplyTimeout { get; set; }

    /// <inheritdoc />
    public DateTime LatestDeliveredTimestamp { get; private set; } = DateTime.MinValue;

    /// <inheritdoc />
    public void NoteDelivered(DateTime morganaTimestamp)
    {
        // Replies may be recovered out of order, so only a later stamp moves the mark.
        if (morganaTimestamp > LatestDeliveredTimestamp)
            LatestDeliveredTimestamp = morganaTimestamp;
    }

    /// <inheritdoc />
    public string ComposerText { get; set; } = string.Empty;

    /// <summary>What a turn put on screen and the words it took from the composer, kept so that the turn can be undone.</summary>
    private sealed record OpenTurn(ChatMessage UserMessage, ChatMessage? PressedOffering, string WordsToGiveBack);

    /// <summary>The turn that <see cref="BeginTurn"/> opened and nothing has closed yet; null while none is open.</summary>
    private OpenTurn? openTurn;

    /// <inheritdoc />
    public void BeginTurn(string text, ChatMessage? pressedOffering = null)
    {
        // A press sends the button's value, which is not the user's words: only a typed turn has words to give back
        string wordsToGiveBack = pressedOffering != null ? string.Empty : text;

        // One turn at a time: the composer stays shut until the answer arrives or the turn is withdrawn
        IsSending = true;

        // Echoed locally and immediately: the user sees their own words without waiting for the server, which never sends them back.
        ChatMessage userMessage = new ChatMessage
        {
            ConversationId = ConversationId,
            Text = text,
            Type = MessageType.User,
            Timestamp = DateTime.UtcNow
        };
        ChatMessages.Add(userMessage);

        // Shown immediately: the real answer arrives over SignalR, so without this the UI would sit inert while the agent thinks.
        AddTypingIndicator();

        // What a refusal of this turn would have to undo, until the next turn replaces it
        openTurn = new OpenTurn(userMessage, pressedOffering, wordsToGiveBack);
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

        // The row of buttons that the press answered becomes pressable again
        if (openTurn.PressedOffering != null)
            openTurn.PressedOffering.SelectedQuickReplyId = null;

        // Typed words return to the composer so the user does not retype them
        ComposerText = openTurn.WordsToGiveBack;
        IsSending = false;
        openTurn = null;
    }

    /// <inheritdoc />
    public void AddTypingIndicator()
    {
        // It lives in the message list rather than beside it, so it holds the spot the real answer will take and the
        // conversation does not jump when it is swapped out.
        ChatMessages.Add(new ChatMessage
        {
            ConversationId = ConversationId,
            Type = MessageType.Assistant,
            IsTyping = true,
            AgentName = CurrentAgentName,
            Timestamp = DateTime.UtcNow
        });
    }

    /// <inheritdoc />
    public bool RemoveTypingIndicator()
    {
        // Last, not first: only the most recent turn can legitimately still be waiting
        ChatMessage? typingMessage = ChatMessages.LastOrDefault(message => message.IsTyping);

        // No indicator is on screen, so nothing was removed
        if (typingMessage == null)
            return false;

        // The indicator gives way to whatever comes next
        ChatMessages.Remove(typingMessage);
        return true;
    }

    /// <inheritdoc />
    public void AddChatError(string text, string errorReason, int? fadingDurationSeconds = 10)
    {
        // The banner carries the technical detail and fades on its own
        AddErrorBanner(text, errorReason, fadingDurationSeconds);

        // The chat line stays and is deliberately vague: it marks where the conversation broke without leaving a status
        // code in the transcript.
        ChatMessages.Add(new ChatMessage
        {
            ConversationId = ConversationId,
            Text = "Sorry, an error occurred. Please try again.",
            Timestamp = DateTime.UtcNow,
            AgentName = CurrentAgentName,
            Type = MessageType.Error
        });
    }

    /// <inheritdoc />
    public void AddErrorBanner(string text, string errorReason, int? fadingDurationSeconds = 10)
    {
        // The banner fades on its own and never reaches the conversation
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

    /// <inheritdoc />
    public void ClearErrorMessages()
    {
        // The dust-exhaustion banner is no retryable error but the mark of a spent conversation: cleared, it would leave the
        // user with a locked input and no explanation, so it stays until a brand-new conversation replaces all state.
        TemporaryMessages.RemoveAll(message =>
            string.Equals(message.MessageType, ChannelMessageTypes.Error, StringComparison.OrdinalIgnoreCase)
             && !string.Equals(message.ErrorReason, ChannelErrorReasons.DustBudgetExhausted, StringComparison.Ordinal));
    }

    /// <inheritdoc />
    public void DismissTemporaryMessage(ChannelMessage message)
    {
        // The banner the user closed, or that faded, leaves the screen
        TemporaryMessages.Remove(message);
    }

    /// <inheritdoc />
    public bool IsSpecializedAgent(string? agentName)
    {
        // Specialists are named "Morgana (Billing)" and the base one plain "Morgana", so the parentheses are the whole test.
        return agentName != null && agentName.Contains('(') && agentName.Contains(')');
    }

    /// <inheritdoc />
    // The stylesheet colours each class: a specialist wears the secondary colour and base Morgana the primary one.
    public string GetAgentClass(string? agentName) =>
        IsSpecializedAgent(agentName) ? "agent" : "morgana";

    /// <inheritdoc />
    public bool UpdateAgentFromMessage(ChannelMessage message)
    {
        // A completed agent hands control back, so the header returns to base Morgana even though the message itself was
        // signed by the specialist that just finished.
        string newAgentName = message.AgentCompleted || string.IsNullOrEmpty(message.AgentName)
            ? BaseAgentName
            : message.AgentName;

        // The same agent goes on holding the conversation, so nothing changes
        if (string.Equals(CurrentAgentName, newAgentName, StringComparison.OrdinalIgnoreCase))
            return false;

        // The header names the new holder
        CurrentAgentName = newAgentName;
        return true;
    }

    /// <inheritdoc />
    // A live message has no IsLastHistoryMessage, while of a resumed history only the last message is still actionable.
    public bool HasActiveQuickReplies() =>
        ChatMessages.Any(message => message is { QuickReplies: { Count: > 0 }, SelectedQuickReplyId: null, IsLastHistoryMessage: not false });

    /// <inheritdoc />
    public bool HasTypingIndicator() =>
        ChatMessages.Any(message => message.IsTyping);

    /// <inheritdoc />
    public bool IsConversationDeadFromDust() =>
        TemporaryMessages.Any(message =>
            string.Equals(message.ErrorReason, ChannelErrorReasons.DustBudgetExhausted, StringComparison.Ordinal));

    /// <inheritdoc />
    public void Reset()
    {
        // Everything goes back to how a fresh circuit starts.
        ChatMessages.Clear();
        TemporaryMessages.Clear();
        ConversationId = string.Empty;
        CurrentAgentName = BaseAgentName;
        IsConnected = false;
        IsSending = false;
        IsInitialized = false;
        HasCheckedStorage = false;
        DustLevel = null;
        ReplyTimeout = null;
        LatestDeliveredTimestamp = DateTime.MinValue;
    }
}