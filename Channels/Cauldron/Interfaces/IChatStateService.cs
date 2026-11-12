using Cauldron.Messages;
using Morgana.Contracts;

namespace Cauldron.Interfaces;

/// <summary>The chat UI state of one circuit: the messages, the banners, the agent on screen and the flags that gate the input.</summary>
public interface IChatStateService
{
    /// <summary>Every message of the conversation on screen, typing indicator included.</summary>
    List<ChatMessage> ChatMessages { get; }

    /// <summary>The banners that fade on their own, such as a rate limit or a failed send.</summary>
    List<ChannelMessage> TemporaryMessages { get; }

    /// <summary>The conversation on screen.</summary>
    string ConversationId { get; set; }

    /// <summary>The agent the header names: <c>Morgana</c> or a specialist such as <c>Morgana (Billing)</c>.</summary>
    string CurrentAgentName { get; set; }

    /// <summary>True while the hub connection is up and replies can arrive.</summary>
    bool IsConnected { get; set; }

    /// <summary>True while a turn is in flight, which keeps the composer shut so a turn is never sent twice.</summary>
    bool IsSending { get; set; }

    /// <summary>True once the hub connection and the conversation's start or resume have succeeded.</summary>
    bool IsInitialized { get; set; }

    /// <summary>True once the browser's storage has been read for a conversation to resume.</summary>
    bool HasCheckedStorage { get; set; }

    /// <summary>The remaining dust as a fraction of the budget, from 1.0 down to 0.0, as Morgana last reported it; null when Morgana limits none and the gauge is hidden.</summary>
    double? DustLevel { get; set; }

    /// <summary>How long a turn may stay silent before it is given up on, as Morgana last reported it; null until she has.</summary>
    TimeSpan? ReplyTimeout { get; set; }

    /// <summary>The latest timestamp that Morgana wrote on a reply this client was given, pushed or recovered.</summary>
    // A catch-up compares the history with it: both sides come from Morgana's clock, so a skewed clock here cannot hide or repeat a reply.
    DateTime LatestDeliveredTimestamp { get; }

    /// <summary>Records that a reply Morgana stamped at <paramref name="morganaTimestamp"/> is on screen.</summary>
    // Only a message Morgana also keeps in the conversation's history belongs here, never a transient warning or anything
    // stamped by this client.
    void NoteDelivered(DateTime morganaTimestamp);

    /// <summary>The words in the composer, held as chat state so that a withdrawn turn can give them back.</summary>
    string ComposerText { get; set; }

    /// <summary>Opens a turn on screen: raises the send flag and adds the user's message and the typing indicator.</summary>
    // pressedOffering is the message whose quick reply the turn presses; null for a typed turn.
    void BeginTurn(string text, ChatMessage? pressedOffering = null);

    /// <summary>Undoes the turn that <see cref="BeginTurn"/> opened, leaving the screen as it was before the send; nothing happens when no turn is open.</summary>
    void WithdrawTurn();

    /// <summary>Adds a typing indicator for the agent on screen.</summary>
    void AddTypingIndicator();

    /// <summary>Removes the latest typing indicator, telling whether there was one.</summary>
    bool RemoveTypingIndicator();

    /// <summary>Reports an error both as a fading banner carrying the detail and as a lasting line in the conversation.</summary>
    void AddChatError(string text, string errorReason, int? fadingDurationSeconds = 10);

    /// <summary>Reports an error as a fading banner alone, leaving the conversation untouched.</summary>
    void AddErrorBanner(string text, string errorReason, int? fadingDurationSeconds = 10);

    /// <summary>Removes every error banner except the one saying that the budget is spent.</summary>
    void ClearErrorMessages();

    /// <summary>Dismisses one banner.</summary>
    void DismissTemporaryMessage(ChannelMessage message);

    /// <summary>Names in the header the agent that <paramref name="message"/> leaves holding the conversation, telling whether it changed.</summary>
    bool UpdateAgentFromMessage(ChannelMessage message);

    /// <summary>Tells whether <paramref name="agentName"/> names a specialist, such as <c>Morgana (Billing)</c>, rather than base Morgana.</summary>
    bool IsSpecializedAgent(string? agentName);

    /// <summary>The CSS class of an agent's elements: <c>agent</c> for a specialist and <c>morgana</c> for base Morgana.</summary>
    string GetAgentClass(string? agentName);

    /// <summary>Tells whether quick replies are still waiting for a press, which keeps the composer shut.</summary>
    bool HasActiveQuickReplies();

    /// <summary>Tells whether a typing indicator is on screen.</summary>
    bool HasTypingIndicator();

    /// <summary>Tells whether the conversation is spent, which a dust-exhaustion banner on screen says.</summary>
    // The only way forward is a brand-new conversation, so the New Conversation button stays reachable even when the usual
    // connection, history and initialization gates would hide it.
    bool IsConversationDeadFromDust();

    /// <summary>Forgets everything, for a fresh conversation.</summary>
    void Reset();
}