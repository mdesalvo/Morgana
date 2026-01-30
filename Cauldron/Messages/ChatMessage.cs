namespace Cauldron.Messages;

/// <summary>
/// Represents a chat message in the Cauldron UI.
/// Used to display user messages, agent responses, typing indicators, and error messages.
/// </summary>
/// <remarks>
/// <para><strong>Message Lifecycle:</strong></para>
/// <list type="number">
/// <item>User types message → ChatMessage created with Type = User</item>
/// <item>Typing indicator shown → ChatMessage created with IsTyping = true</item>
/// <item>Agent responds via SignalR → ChatMessage created with Type = Assistant</item>
/// <item>Optional quick replies attached → QuickReplies list populated</item>
/// <item>User clicks quick reply → SelectedQuickReplyId set, all buttons disabled</item>
/// </list>
/// <para><strong>Message Types:</strong></para>
/// <list type="bullet">
/// <item><term>User</term><description>Message from the user</description></item>
/// <item><term>Assistant</term><description>Regular response from agent</description></item>
/// <item><term>Presentation</term><description>Welcome/completion message with special styling</description></item>
/// <item><term>Error</term><description>Error message with error styling</description></item>
/// </list>
/// </remarks>
public class ChatMessage
{
    /// <summary>
    /// Unique identifier of the conversation this message belongs to.
    /// </summary>
    public string ConversationId { get; set; } = string.Empty;

    /// <summary>
    /// Message text content displayed to the user.
    /// </summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// Timestamp when the message was created or received.
    /// Displayed in HH:mm format in the UI.
    /// </summary>
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// Type of message determining styling and behavior.
    /// </summary>
    public MessageType Type { get; set; }

    /// <summary>
    /// Optional list of quick reply buttons attached to this message.
    /// Only applicable for assistant messages (typically presentation messages).
    /// </summary>
    public List<QuickReply>? QuickReplies { get; set; }

    /// <summary>
    /// Optional error reason if Type = Error.
    /// Contains technical error details for debugging.
    /// </summary>
    public string? ErrorReason { get; set; }

    /// <summary>
    /// Name of the agent that generated this message.
    /// Examples: "Morgana", "Morgana (Billing)", "Morgana (Contract)", ...
    /// Used to display agent avatar and header color.
    /// </summary>
    public string AgentName { get; set; } = "Morgana";

    /// <summary>
    /// Indicates whether the agent has completed its task after this message.
    /// When true, the UI returns to idle state (header shows "Morgana", purple color).
    /// </summary>
    public bool AgentCompleted { get; set; } = false;

    /// <summary>
    /// Tracks which quick reply button was clicked by the user.
    /// When set, all quick reply buttons are disabled and the selected one shows a checkmark.
    /// </summary>
    public string? SelectedQuickReplyId { get; set; }

    /// <summary>
    /// Gets or sets the message role for CSS styling ("user" or "assistant").
    /// Automatically converts between MessageType enum and string role.
    /// </summary>
    /// <remarks>
    /// <para><strong>Conversion Logic:</strong></para>
    /// <code>
    /// MessageType.User → "user"
    /// MessageType.Assistant → "assistant"
    /// MessageType.Presentation → "assistant"
    /// MessageType.Error → "assistant"
    /// </code>
    /// </remarks>
    public string Role
    {
        get => Type switch
        {
            MessageType.User => "user",
            _ => "assistant"
        };
        set => Type = value switch
        {
            "user" => MessageType.User,
            _ => MessageType.Assistant
        };
    }

    /// <summary>
    /// Indicates whether this message is a typing indicator.
    /// When true, displays animated three-dot typing indicator instead of text.
    /// </summary>
    /// <remarks>
    /// Typing indicators are temporary messages removed when the actual agent response arrives.
    /// </remarks>
    public bool IsTyping { get; set; }

    /// <summary>
    /// Indicates whether this message represents an error.
    /// When true, message displays with error styling (red/warning appearance).
    /// </summary>
    public bool IsError { get; set; }

    /// <summary>
    /// Optional flag indicating that this is the last message of a resumed conversation.
    /// </summary>
    public bool? IsLastHistoryMessage { get; init; }

    /// <summary>
    /// Indicates whether this message is currently being streamed from the backend.
    /// When true, the message text is progressively updated by the typewriter effect.
    /// When false, the message is complete and immutable.
    /// </summary>
    public bool IsStreaming { get; set; }
}

/// <summary>
/// Enumeration of message types for styling and behavior differentiation.
/// </summary>
public enum MessageType
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
    Assistant,

    /// <summary>
    /// Welcome or completion message with special styling.
    /// Typically includes quick reply buttons for user interaction.
    /// </summary>
    Presentation,

    /// <summary>
    /// Error message with error styling.
    /// Displayed when backend errors occur or exceptions are caught.
    /// </summary>
    Error
}