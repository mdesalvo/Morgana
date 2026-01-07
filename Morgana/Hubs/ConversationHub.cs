using Microsoft.AspNetCore.SignalR;

namespace Morgana.Hubs;

/// <summary>
/// SignalR hub for real-time bi-directional communication between clients and the Morgana conversation system.
/// Manages conversation group membership and provides connection lifecycle hooks.
/// </summary>
/// <remarks>
/// <para><strong>Architecture Overview:</strong></para>
/// <list type="bullet">
/// <item><term>Group-based routing</term><description>Each conversation is a SignalR group for targeted message delivery</description></item>
/// <item><term>Connection management</term><description>Tracks client connections and handles graceful disconnections</description></item>
/// <item><term>Server-to-client messaging</term><description>Actor system sends messages to clients via IHubContext</description></item>
/// </list>
/// <para><strong>Communication Flow:</strong></para>
/// <code>
/// Client → HTTP POST /api/conversation/start
/// Client → SignalR JoinConversation(conversationId)
/// Client → HTTP POST /api/conversation/{id}/message
/// Server → Actor Pipeline → IHubContext → SignalR Group → Client receives message
/// </code>
/// <para><strong>Group Management:</strong></para>
/// <para>Clients must join a conversation group to receive messages. Multiple clients can join the same conversation
/// for collaborative scenarios (e.g., customer + support agent both seeing the AI conversation).</para>
/// </remarks>
public class ConversationHub : Hub
{
    private readonly ILogger logger;

    /// <summary>
    /// Initializes a new instance of the ConversationHub.
    /// </summary>
    /// <param name="logger">Logger instance for connection tracking and diagnostics</param>
    public ConversationHub(ILogger logger)
    {
        this.logger = logger;
    }

    /// <summary>
    /// Adds the current client connection to a conversation group.
    /// Clients must join a conversation group to receive messages from the actor system.
    /// </summary>
    /// <param name="conversationId">Unique identifier of the conversation to join</param>
    /// <returns>Task representing the async operation</returns>
    /// <remarks>
    /// <para><strong>Client-side invocation example (JavaScript):</strong></para>
    /// <code>
    /// await connection.invoke("JoinConversation", conversationId);
    /// </code>
    /// <para><strong>Group behavior:</strong></para>
    /// <list type="bullet">
    /// <item>Multiple clients can join the same conversation</item>
    /// <item>All clients in the group receive messages sent to that conversation</item>
    /// <item>Useful for collaborative scenarios (multi-user conversations, monitoring dashboards)</item>
    /// </list>
    /// <para><strong>Best practice:</strong> Call this immediately after starting a conversation via REST API.</para>
    /// </remarks>
    public async Task JoinConversation(string conversationId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, conversationId);

        logger.LogInformation($"Client {Context.ConnectionId} joined conversation {conversationId}");
    }

    /// <summary>
    /// Removes the current client connection from a conversation group.
    /// Client will no longer receive messages for this conversation.
    /// </summary>
    /// <param name="conversationId">Unique identifier of the conversation to leave</param>
    /// <returns>Task representing the async operation</returns>
    /// <remarks>
    /// <para><strong>Client-side invocation example (JavaScript):</strong></para>
    /// <code>
    /// await connection.invoke("LeaveConversation", conversationId);
    /// </code>
    /// <para><strong>Use cases:</strong></para>
    /// <list type="bullet">
    /// <item>User navigates away from conversation</item>
    /// <item>Conversation ended via REST API</item>
    /// <item>Client wants to stop receiving updates without disconnecting entirely</item>
    /// </list>
    /// <para><strong>Note:</strong> Groups are automatically cleaned up on disconnection,
    /// but explicit leaving is recommended for clean state management.</para>
    /// </remarks>
    public async Task LeaveConversation(string conversationId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, conversationId);

        logger.LogInformation($"Client {Context.ConnectionId} left conversation {conversationId}");
    }

    /// <summary>
    /// Connection lifecycle hook: called when a client establishes a SignalR connection.
    /// </summary>
    /// <returns>Task representing the async operation</returns>
    /// <remarks>
    /// <para>Logged for diagnostics and connection tracking.</para>
    /// <para><strong>Connection flow:</strong></para>
    /// <list type="number">
    /// <item>Client establishes WebSocket/Server-Sent Events connection</item>
    /// <item>OnConnectedAsync is called (this method)</item>
    /// <item>Client can now invoke hub methods (JoinConversation, etc.)</item>
    /// <item>Server can send messages to client via IHubContext</item>
    /// </list>
    /// </remarks>
    public override async Task OnConnectedAsync()
    {
        logger.LogInformation($"Client connected: {Context.ConnectionId}");
        await base.OnConnectedAsync();
    }

    /// <summary>
    /// Connection lifecycle hook: called when a client disconnects from the SignalR hub.
    /// Handles both graceful disconnections and unexpected connection drops.
    /// </summary>
    /// <param name="exception">Exception that caused the disconnection, if any (null for graceful disconnects)</param>
    /// <returns>Task representing the async operation</returns>
    /// <remarks>
    /// <para><strong>Automatic cleanup:</strong></para>
    /// <list type="bullet">
    /// <item>SignalR automatically removes the connection from all groups</item>
    /// <item>No manual cleanup of group memberships needed</item>
    /// </list>
    /// <para><strong>Exception handling:</strong></para>
    /// <para>If exception is not null, the disconnection was unexpected (network failure, timeout, etc.).
    /// This is logged for diagnostics but doesn't affect conversation state in the actor system.</para>
    /// </remarks>
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        logger.LogInformation($"Client disconnected: {Context.ConnectionId}");
        await base.OnDisconnectedAsync(exception);
    }
}