using Microsoft.AspNetCore.SignalR;
using Morgana.AI;
using Morgana.AI.Interfaces;

namespace Morgana.Web.Hubs;

/// <summary>
/// SignalR hub for real-time bidirectional communication between clients and actor system.
/// Implements group-based routing (one group per conversation) so clients join to receive messages.
/// Admits a connection only from a channel carrying a valid bearer token, as the REST API does.
/// </summary>
public class MorganaHub : Hub
{
    /// <summary>Scheme a channel's token is presented under in the Authorization header.</summary>
    private const string BearerPrefix = "Bearer ";

    /// <summary>Query parameter the SignalR client carries its token in, since a WebSocket upgrade cannot set headers.</summary>
    private const string AccessTokenQueryKey = "access_token";

    /// <summary>Where the issuer proved at connection time is kept for every later call on the connection.</summary>
    private const string IssuerItemKey = "morgana.channel.issuer";

    private readonly ILogger logger;
    private readonly IAuthenticationService authenticationService;
    private readonly IConversationSealService conversationSealService;
    private readonly IConversationPersistenceService conversationPersistenceService;

    /// <summary>
    /// Initializes a new instance of the MorganaHub.
    /// </summary>
    public MorganaHub(
        ILogger logger,
        IAuthenticationService authenticationService,
        IConversationSealService conversationSealService,
        IConversationPersistenceService conversationPersistenceService)
    {
        this.logger = logger;
        this.authenticationService = authenticationService;
        this.conversationSealService = conversationSealService;
        this.conversationPersistenceService = conversationPersistenceService;
    }

    /// <summary>
    /// Adds current connection to conversation group for message delivery.
    /// Multiple clients can join same conversation; all receive group messages.
    /// </summary>
    /// <remarks>
    /// <strong>Call this BEFORE <c>POST api/morgana/conversation/start</c>.</strong> The channel mints the
    /// conversation id and a group is just a string key, so joining first is always possible; it is also
    /// the only ordering that works: the presentation is sent the moment the conversation starts and
    /// SignalR discards a group send with no members without a trace. That join carries no seal, since
    /// none exists yet: it is no opening, because it needs an id nobody has been shown.
    /// </remarks>
    /// <param name="conversationId">The conversation whose deliveries the connection receives</param>
    /// <param name="seal">The conversation's seal, required once it is on record; null before its start</param>
    /// <exception cref="HubException">The conversation is on record and the seal is missing, wrong or another channel's</exception>
    public async Task JoinConversation(string conversationId, string? seal)
    {
        // A conversation on record is followed only by the channel holding its seal, exactly as it is read over REST
        if (conversationPersistenceService.ConversationExists(conversationId)
            && !await conversationSealService.VerifyAsync(conversationId, Context.Items[IssuerItemKey] as string ?? string.Empty, seal))
        {
            logger.LogWarning("Client {ContextConnectionId} refused on conversation {ConversationId}: seal not presented", Context.ConnectionId, conversationId);
            throw new HubException("Conversation not found");
        }

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
    /// Admits a connection carrying a channel's valid token and keeps its issuer for the joins that follow;
    /// any other connection is closed at once.
    /// </summary>
    public override async Task OnConnectedAsync()
    {
        // The browser client sends the token in the query string, a header-capable client in Authorization
        HttpContext? httpContext = Context.GetHttpContext();
        string? token = httpContext?.Request.Query[AccessTokenQueryKey].FirstOrDefault();
        string authorizationHeader = httpContext?.Request.Headers.Authorization.ToString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(token) && authorizationHeader.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
            token = authorizationHeader[BearerPrefix.Length..].Trim();

        // Fail-closed like the REST gate: no token, a token no issuer's key proves or a partner's all close the connection
        Records.AuthenticationResult authResult = string.IsNullOrWhiteSpace(token)
            ? new Records.AuthenticationResult(false, Error: "Missing access token")
            : await authenticationService.AuthenticateAsync(token);
        if (!authResult.IsAuthenticated || authResult.IsPartner)
        {
            logger.LogWarning("Client {ContextConnectionId} refused: {Error}", Context.ConnectionId,
                authResult.IsPartner ? "a partner's token opens no channel connection" : authResult.Error);
            Context.Abort();
            return;
        }

        Context.Items[IssuerItemKey] = authResult.Issuer;

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
