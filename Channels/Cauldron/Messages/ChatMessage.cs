using Cauldron.Services;
using Morgana.Contracts;

namespace Cauldron.Messages;

/// <summary>One row of the chat as the UI knows it.</summary>
// Deliberately apart from the wire contracts: it is mutable, it carries state the server never sees (typing indicator,
// streaming flag, the quick reply that was picked) and it covers rows the server never sent, such as the local error line.
public class ChatMessage
{
    /// <summary>The conversation the row belongs to.</summary>
    public string ConversationId { get; set; } = string.Empty;

    /// <summary>The text of the row, revealed progressively while it streams.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>When the row was written: Morgana's stamp for a reply, this client's for a row it wrote itself.</summary>
    public DateTime Timestamp { get; set; }

    /// <summary>What kind of row it is, which decides its side and styling.</summary>
    public MessageType Type { get; set; }

    /// <summary>The quick replies offered with the row; null when it offers none.</summary>
    public List<QuickReply>? QuickReplies { get; set; }

    /// <summary>The rich card drawn between the row's text and its quick replies; null when it carries none.</summary>
    public RichCard? RichCard { get; set; }

    /// <summary>The agent that wrote the row, such as <c>Morgana</c> or <c>Morgana (Billing)</c>, which decides the avatar's colour.</summary>
    public string AgentName { get; set; } = ChatStateService.BaseAgentName;

    /// <summary>The quick reply that was pressed on the row; null while none was.</summary>
    // Once set, the row's buttons are disabled and the pressed one shows a checkmark.
    public string? SelectedQuickReplyId { get; set; }

    /// <summary>The stylesheet class of a row the visitor wrote, matching <c>.message.user</c>.</summary>
    private const string UserRoleClass = "user";

    /// <summary>The stylesheet class of every other row, matching <c>.message.assistant</c>.</summary>
    private const string AssistantRoleClass = "assistant";

    /// <summary>The row's side as the stylesheet wants it, projected from <see cref="Type"/>.</summary>
    // Everything that is not a user message renders on the assistant side, error lines included.
    public string Role
        => Type == MessageType.User ? UserRoleClass : AssistantRoleClass;

    /// <summary>True for the typing indicator, which holds the spot of the reply on its way.</summary>
    public bool IsTyping { get; set; }

    /// <summary>True for the last row of a resumed history, false for any earlier one and null for a row that arrived live.</summary>
    // Of a resumed history only the last row's quick replies can still be pressed.
    public bool? IsLastHistoryMessage { get; init; }

    /// <summary>True while the typewriter is still revealing the row's text.</summary>
    public bool IsStreaming { get; set; }
}

/// <summary>What kind of row a chat message is.</summary>
public enum MessageType
{
    /// <summary>The visitor's own words, on the right.</summary>
    User,

    /// <summary>A reply from Morgana or an agent, on the left beside the avatar.</summary>
    Assistant,

    /// <summary>The local line marking where a failure broke the conversation.</summary>
    Error
}