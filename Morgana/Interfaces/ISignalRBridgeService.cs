using static Morgana.Records;

namespace Morgana.Interfaces;

public interface ISignalRBridgeService
{
    Task SendMessageToConversationAsync(string conversationId, string text, string? errorReason = null);
    
    Task SendStructuredMessageAsync(
        string conversationId,
        string text,
        string messageType,
        List<QuickReply>? quickReplies = null,
        string? errorReason = null);
}