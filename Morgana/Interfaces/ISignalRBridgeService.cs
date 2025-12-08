namespace Morgana.Interfaces;

public interface ISignalRBridgeService
{
    Task SendMessageToConversationAsync(string conversationId, string userId, string text, string? errorReason = null);
}