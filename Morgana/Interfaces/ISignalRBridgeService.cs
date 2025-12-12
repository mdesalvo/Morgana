namespace Morgana.Interfaces;

public interface ISignalRBridgeService
{
    Task SendMessageToConversationAsync(string conversationId, string text, string? errorReason = null);
}