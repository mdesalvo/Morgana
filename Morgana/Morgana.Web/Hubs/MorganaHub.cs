using Microsoft.AspNetCore.SignalR;

namespace Morgana.Web.Hubs;

/// <summary>
/// The SignalR hub through which a channel joins the group of a conversation, one group per conversation id.
/// Morgana pushes to the group, never to a connection, so every client that joined a conversation receives its messages.
/// </summary>
public class MorganaHub : Hub
{
    /// <summary>Records connections and the groups they join and leave.</summary>
    private readonly ILogger logger;

    /// <summary>Binds the hub to its logger.</summary>
    /// <param name="logger">Records connections and groups.</param>
    public MorganaHub(ILogger logger)
    {
        this.logger = logger;
    }

    /// <summary>
    /// Adds the connection to the conversation's group, which a channel must do before it calls <c>POST api/morgana/conversation/start</c>.
    /// The welcome is sent the moment the conversation starts and SignalR discards a send to an empty group without a trace;
    /// the channel mints the id, so joining first is always possible.
    /// </summary>
    public async Task JoinConversation(string conversationId)
    {
        // The group is the conversation id, the key every push of this conversation is addressed to.
        await Groups.AddToGroupAsync(Context.ConnectionId, conversationId);

        logger.LogInformation("Client {ContextConnectionId} joined conversation {ConversationId}", Context.ConnectionId, conversationId);
    }

    /// <summary>
    /// Removes the connection from the conversation's group, as Cauldron does when it moves to another conversation on the same connection.
    /// A connection that closes leaves its groups by itself.
    /// </summary>
    public async Task LeaveConversation(string conversationId)
    {
        // The connection stays open for the conversation the channel moves to, but no longer hears this one.
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, conversationId);

        logger.LogInformation("Client {ContextConnectionId} left conversation {ConversationId}", Context.ConnectionId, conversationId);
    }

    /// <summary>Records a new connection, which receives nothing until it joins a conversation's group.</summary>
    public override async Task OnConnectedAsync()
    {
        logger.LogInformation("Client connected: {ContextConnectionId}", Context.ConnectionId);
        await base.OnConnectedAsync();
    }

    /// <summary>
    /// Records a closed connection, which SignalR has already removed from every group.
    /// A message pushed meanwhile is lost to it, which the client repairs from the history when it comes back.
    /// </summary>
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        logger.LogInformation("Client disconnected: {ContextConnectionId}", Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }
}