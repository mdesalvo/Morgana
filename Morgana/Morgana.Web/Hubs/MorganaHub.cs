using Microsoft.AspNetCore.SignalR;

namespace Morgana.Web.Hubs;

/// <summary>
/// SignalR hub for real-time bidirectional communication between clients and actor system.
/// Implements group-based routing (one group per conversation) so clients join to receive messages.
/// Provides connection lifecycle hooks to track client connections and disconnections.
/// </summary>
public class MorganaHub : Hub
{
    private readonly ILogger logger;

    /// <summary>
    /// Initializes a new instance of the MorganaHub.
    /// </summary>
    /// <param name="logger">Logger instance for connection tracking and diagnostics</param>
    public MorganaHub(ILogger logger)
    {
        this.logger = logger;
    }

    /// <summary>
    /// Adds current connection to conversation group for message delivery.
    /// Multiple clients can join same conversation; all receive group messages.
    /// Call immediately after REST API conversation start.
    /// </summary>
    public async Task JoinConversation(string conversationId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, conversationId);

        logger.LogInformation("Client {ContextConnectionId} joined conversation {ConversationId}", Context.ConnectionId, conversationId);
    }

    /// <summary>
    /// Removes current connection from conversation group.
    /// Client stops receiving messages for this conversation.
    /// Useful for navigation without full disconnect (groups auto-cleanup on disconnect).
    /// </summary>
    public async Task LeaveConversation(string conversationId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, conversationId);

        logger.LogInformation("Client {ContextConnectionId} left conversation {ConversationId}", Context.ConnectionId, conversationId);
    }

    /// <summary>
    /// Lifecycle hook called when a client establishes a SignalR connection.
    /// Logs connection for diagnostics. Client can then invoke hub methods (JoinConversation, LeaveConversation).
    /// </summary>
    public override async Task OnConnectedAsync()
    {
        logger.LogInformation("Client connected: {ContextConnectionId}", Context.ConnectionId);
        await base.OnConnectedAsync();
    }

    /// <summary>
    /// Lifecycle hook called when a client disconnects from the SignalR hub (graceful or unexpected).
    /// SignalR automatically removes the connection from all groups; no manual cleanup needed.
    /// Logs disconnection for diagnostics; exception param indicates unexpected disconnect (network failure, timeout, etc.).
    /// </summary>
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        logger.LogInformation("Client disconnected: {ContextConnectionId}", Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }
}