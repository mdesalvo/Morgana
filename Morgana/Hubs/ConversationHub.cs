using Microsoft.AspNetCore.SignalR;

namespace Morgana.Hubs;

public class ConversationHub : Hub
{
    private readonly ILogger<ConversationHub> _logger;

    public ConversationHub(ILogger<ConversationHub> logger)
    {
        _logger = logger;
    }

    public async Task JoinConversation(string conversationId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, conversationId);

        _logger.LogInformation($"Client {Context.ConnectionId} joined conversation {conversationId}");
    }

    public async Task LeaveConversation(string conversationId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, conversationId);

        _logger.LogInformation($"Client {Context.ConnectionId} left conversation {conversationId}");
    }

    public override async Task OnConnectedAsync()
    {
        _logger.LogInformation($"Client connected: {Context.ConnectionId}");
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation($"Client disconnected: {Context.ConnectionId}");
        await base.OnDisconnectedAsync(exception);
    }
}