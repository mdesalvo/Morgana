using Microsoft.AspNetCore.SignalR;
using Morgana.Hubs;
using Morgana.Interfaces;

namespace Morgana.Services;

public class SignalRBridgeService : ISignalRBridgeService
{
    private readonly IHubContext<ConversationHub> _hubContext;
    private readonly ILogger<SignalRBridgeService> _logger;

    public SignalRBridgeService(IHubContext<ConversationHub> hubContext, ILogger<SignalRBridgeService> logger)
    {
        _hubContext = hubContext;
        _logger = logger;
    }

    public async Task SendMessageToConversationAsync(string conversationId, string text, string? errorReason = null)
    {
        _logger.LogInformation($"Sending message to conversation {conversationId} via SignalR");

        await _hubContext.Clients.Group(conversationId).SendAsync("ReceiveMessage", new
        {
            conversationId,
            text,
            timestamp = DateTime.UtcNow,
            errorReason
        });
    }
}