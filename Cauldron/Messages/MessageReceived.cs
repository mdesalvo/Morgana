namespace Cauldron.Messages;

public class MessageReceived
{
    public string ConversationId { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public string? ErrorReason { get; set; }
}