namespace Cauldron.Messages;


public class ChatMessage
{
    public string ConversationId { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public MessageType Type { get; set; }
    public List<QuickReply>? QuickReplies { get; set; }
    public string? ErrorReason { get; set; }
    public string AgentName { get; set; } = "Morgana";
    public bool AgentCompleted { get; set; } = false;
    
    // Track which quick reply was clicked (if any)
    public string? SelectedQuickReplyId { get; set; }
    
    public string Role
    {
        get => Type switch
        {
            MessageType.User => "user",
            MessageType.Assistant => "assistant",
            MessageType.Presentation => "assistant",
            MessageType.Error => "assistant",
            _ => "assistant"
        };
        set => Type = value switch
        {
            "user" => MessageType.User,
            "assistant" => MessageType.Assistant,
            _ => MessageType.Assistant
        };
    }
    
    // For typing indicator
    public bool IsTyping { get; set; }
    
    // For error messages
    public bool IsError { get; set; }
}

public enum MessageType
{
    User,
    Assistant,
    Presentation,
    Error
}