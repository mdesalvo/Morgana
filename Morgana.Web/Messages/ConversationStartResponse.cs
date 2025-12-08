namespace Morgana.Web.Messages;

public class ConversationStartResponse
{
    public string ConversationId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}