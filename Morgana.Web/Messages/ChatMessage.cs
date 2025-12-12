namespace Morgana.Web.Messages;

public class ChatMessage
{
    public string ConversationId { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public string Role { get; set; } = "user";
    public DateTime Timestamp { get; set; }
    public bool IsTyping { get; set; }
    public bool IsError { get; set; }
}