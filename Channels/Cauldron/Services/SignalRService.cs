using System.Diagnostics;
using Cauldron.Handlers;
using Morgana.Contracts;
using Microsoft.AspNetCore.SignalR.Client;

namespace Cauldron.Services;

/// <summary>
/// SignalR client for the Morgana backend: owns the hub connection, its reconnection policy and
/// group membership and republishes what arrives as plain events. This is the only place in
/// Cauldron that touches SignalR — everything else subscribes.
/// </summary>
public class SignalRService : IAsyncDisposable
{
    /// <summary>Supplies Morgana's base address.</summary>
    private readonly IConfiguration configuration;

    /// <summary>Mints the token that authenticates every connection attempt.</summary>
    private readonly MorganaAuthHandler authHandler;

    /// <summary>Paces the first connection attempts and the automatic reconnection.</summary>
    private readonly IRetryPolicy retryPolicy;

    /// <summary>The hub connection; null until started and again after being stopped.</summary>
    private HubConnection? hubConnection;

    /// <summary>Receives the connection lifecycle log lines.</summary>
    private readonly ILogger logger;

    /// <summary>
    /// The conversations this client means to be subscribed to. Group membership belongs to a
    /// connection id and a reconnect mints a new one, so these are joined again on every reconnect.
    /// </summary>
    private readonly HashSet<string> joinedConversations = [];

    /// <summary>
    /// Guards <see cref="joinedConversations"/>: joins and leaves come from the circuit, the
    /// rejoin from the SignalR client's own thread.
    /// </summary>
    private readonly Lock joinedConversationsLock = new();

    /// <summary>
    /// Cancels the attempts to establish the first connection, which may go on for as long as
    /// Morgana is unreachable: stopping the service must end them.
    /// </summary>
    private CancellationTokenSource? startCancellation;

    /// <summary>
    /// Raised for each message the backend delivers on this conversation, already deserialized.
    /// </summary>
    public event Func<ChannelMessage, Task>? OnMessageReceived;

    /// <summary>
    /// Raised for each streaming chunk of an agent response. Chunks are incremental deltas, to be
    /// appended in arrival order; the finished message arrives separately on
    /// <see cref="OnMessageReceived"/> carrying the full metadata.
    /// </summary>
    public event Func<string, Task>? OnStreamChunkReceived;

    /// <summary>
    /// Raised whenever the connection goes up or down. True means connected.
    /// </summary>
    public event Action<bool>? OnConnectionStateChanged;

    /// <summary>
    /// Gets a value indicating whether the SignalR connection is currently active.
    /// </summary>
    public bool IsConnected => hubConnection?.State == HubConnectionState.Connected;

    /// <summary>
    /// Creates the SignalR client of one chat session.
    /// </summary>
    public SignalRService(IConfiguration configuration, MorganaAuthHandler authHandler, IRetryPolicy retryPolicy, ILogger logger)
    {
        this.configuration = configuration;
        this.authHandler = authHandler;
        this.retryPolicy = retryPolicy;
        this.logger = logger;
    }

    /// <summary>
    /// Opens the hub connection and wires up the backend event subscriptions. Completes only once
    /// connected: while Morgana is unreachable it keeps retrying at the pace of the retry policy.
    /// </summary>
    /// <exception cref="OperationCanceledException">The service was stopped before a connection was established.</exception>
    public async Task StartAsync()
    {
        // Idempotent: a connection already exists, so there is nothing to build
        if (hubConnection != null)
            return;

        // Validated at startup, so it is present and absolute
        string apiBaseUrl = configuration["Cauldron:MorganaURL"]!;

        hubConnection = new HubConnectionBuilder()
            .WithUrl($"{apiBaseUrl}/morganaHub", options =>
            {
                // Re-issued on every (re)connect attempt: tokens are short-lived, so the callback
                // has to mint a fresh one rather than capture one at build time.
                options.AccessTokenProvider = () => Task.FromResult<string?>(authHandler.GenerateToken());
            })
            // A dropped connection is retried for as long as the circuit lives, never given up as Closed
            .WithAutomaticReconnect(retryPolicy)
            .Build();

        // Inbound agent messages: deserialized by the client, then handed to subscribers as-is
        hubConnection.On<ChannelMessage>("ReceiveMessage", async (message) =>
        {
            // Scopes the conversation id onto every log line emitted while this message is
            // handled, including the ones downstream subscribers (lifecycle, streaming) write —
            // without threading the id through each of their call signatures.
            using IDisposable? scope = logger.BeginScope(new Dictionary<string, object>
            {
                ["ConversationId"] = message.ConversationId
            });

            logger.LogInformation(
                "📩 SignalR message received: {AgentName} -> {ConversationId} (type: {MessageType}, completed: {AgentCompleted})",
                message.AgentName, message.ConversationId, message.MessageType, message.AgentCompleted);

            // Subscribers put the message on screen; with none, it is dropped
            await (OnMessageReceived?.Invoke(message) ?? Task.CompletedTask);
        });

        // Streaming chunks: forwarded raw and unlogged, they arrive one per token
        hubConnection.On<string>("ReceiveStreamChunk", async (chunkText) =>
        {
            // Subscribers feed the typewriter; with none, the chunk is dropped
            await (OnStreamChunkReceived?.Invoke(chunkText) ?? Task.CompletedTask);
        });

        // Connection gone for good: the reconnect attempts above are exhausted
        hubConnection.Closed += async (error) =>
        {
            logger.LogWarning("❌ SignalR disconnected: {ErrorMessage}", error?.Message ?? "No error");
            OnConnectionStateChanged?.Invoke(false);
            await Task.CompletedTask;
        };

        // Reconnect in progress: reported as offline so the UI stops accepting input
        hubConnection.Reconnecting += async (error) =>
        {
            logger.LogInformation("🔄 SignalR reconnecting: {ErrorMessage}", error?.Message ?? "No error");
            OnConnectionStateChanged?.Invoke(false);
            await Task.CompletedTask;
        };

        // Reconnect succeeded under a new connection id, which belongs to no group. The
        // conversations are joined again before going back online: announced first, the UI would
        // accept a message whose reply is sent to a group this client is no longer in.
        hubConnection.Reconnected += async (connectionId) =>
        {
            logger.LogInformation("✅ SignalR reconnected: {ConnectionId}", connectionId);
            await RejoinConversationsAsync();
            OnConnectionStateChanged?.Invoke(true);
        };

        // Lets StopAsync end a first connection that keeps failing
        startCancellation = new CancellationTokenSource();

        // Completes only once Morgana answers
        await ConnectWithRetriesAsync(hubConnection, startCancellation.Token);
        logger.LogInformation("✅ SignalR connected and listening for messages");

        // Announce the initial state, which subscribers have no other way to observe
        OnConnectionStateChanged?.Invoke(true);
        logger.LogInformation("SignalR started successfully");
    }

    /// <summary>
    /// Stops the SignalR connection gracefully.
    /// </summary>
    public async Task StopAsync()
    {
        // Nothing to stop when no connection was ever built
        if (hubConnection != null)
        {
            // Ends a first connection still being attempted, which would otherwise outlive the circuit
            startCancellation?.Cancel();

            // Closes the connection and releases the groups held on the server
            await hubConnection.StopAsync();
            await hubConnection.DisposeAsync();

            // The attempts are over: the token source has no one left to cancel
            startCancellation?.Dispose();
            startCancellation = null;

            // Cleared so a later StartAsync builds a fresh connection instead of short-circuiting
            hubConnection = null;

            // A fresh connection starts with no conversation: whoever starts it joins what it needs
            lock (joinedConversationsLock)
                joinedConversations.Clear();

            // The page shows Offline now that the connection is gone
            OnConnectionStateChanged?.Invoke(false);
            logger.LogWarning("🛑 SignalR connection stopped");
        }
    }

    /// <summary>
    /// Joins a conversation group, which is what makes this client start receiving that
    /// conversation's messages. The subscription outlives reconnects until it is left.
    /// </summary>
    /// <param name="conversationId">Unique identifier of the conversation to join</param>
    public async Task JoinConversation(string conversationId)
    {
        // Recorded even when the join below cannot go out: a reconnect in progress joins it then
        lock (joinedConversationsLock)
            joinedConversations.Add(conversationId);

        // Connected: the server adds this connection to the conversation's group right away
        if (hubConnection?.State == HubConnectionState.Connected)
        {
            await hubConnection.InvokeAsync("JoinConversation", conversationId);
            logger.LogInformation("✅ Joined SignalR group: {ConversationId}", conversationId);
        }
        else
        {
            // Not connected: nothing reaches the server now. A reconnect in progress joins the
            // conversation once back; a connection closed for good never does.
            logger.LogWarning("⚠️ Cannot join conversation: SignalR not connected");
        }
    }

    /// <summary>
    /// Leaves a conversation group to stop receiving messages for that conversation.
    /// </summary>
    /// <param name="conversationId">Unique identifier of the conversation to leave</param>
    public async Task LeaveConversation(string conversationId)
    {
        // Forgotten even while disconnected, so a later reconnect does not join it again
        lock (joinedConversationsLock)
            joinedConversations.Remove(conversationId);

        // Nothing to leave when disconnected: the group membership died with the connection
        if (hubConnection?.State == HubConnectionState.Connected)
        {
            // The server drops this connection from the group so nothing more is pushed to it
            await hubConnection.InvokeAsync("LeaveConversation", conversationId);
            logger.LogInformation("👋 Left SignalR group: {ConversationId}", conversationId);
        }
    }

    /// <summary>
    /// Attempts the first connection until it succeeds, waiting between failures as the retry
    /// policy dictates. Automatic reconnection covers only a connection that was once established,
    /// so without this a Morgana down at page load would leave the chat offline until a reload.
    /// </summary>
    private async Task ConnectWithRetriesAsync(HubConnection connection, CancellationToken cancellationToken)
    {
        // Handed to the retry policy so it can tell how long Morgana has been unreachable
        Stopwatch elapsedSinceFirstAttempt = Stopwatch.StartNew();

        // Loops until connected: only a cancellation or a policy that gives up leaves it
        for (long previousAttempts = 0; ; previousAttempts++)
        {
            try
            {
                // Success ends the loop: the connection is up
                await connection.StartAsync(cancellationToken);
                return;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // The policy decides how long to wait before the next attempt
                TimeSpan? retryDelay = retryPolicy.NextRetryDelay(new RetryContext
                {
                    PreviousRetryCount = previousAttempts,
                    ElapsedTime = elapsedSinceFirstAttempt.Elapsed,
                    RetryReason = ex
                });

                // A policy that stops retrying hands the last failure back to the caller
                if (retryDelay is null)
                    throw;

                // The reason only, not the stack: while Morgana stays down this repeats every half minute
                logger.LogWarning("⚠️ Morgana unreachable (attempt {Attempt}): {Reason}. Retrying in {RetryDelay}",
                    previousAttempts + 1, ex.Message, retryDelay.Value);

                // Cancelled by StopAsync so a stopped service does not keep retrying
                await Task.Delay(retryDelay.Value, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Joins again every conversation this client was subscribed to before the connection dropped.
    /// A failed join is logged and skipped: should the connection drop again, the next reconnect
    /// retries it.
    /// </summary>
    private async Task RejoinConversationsAsync()
    {
        // Copied under the lock so the joins below run without holding it
        string[] conversationIds;
        lock (joinedConversationsLock)
            conversationIds = [.. joinedConversations];

        // A stop racing the reconnect has already dropped the connection and its subscriptions
        if (hubConnection is not { } connection)
            return;

        // Each conversation is rejoined on its own so one failure does not strand the others
        foreach (string conversationId in conversationIds)
        {
            try
            {
                // The new connection id is in no group until it asks to be
                await connection.InvokeAsync("JoinConversation", conversationId);
                logger.LogInformation("✅ Rejoined SignalR group after reconnect: {ConversationId}", conversationId);
            }
            catch (Exception ex)
            {
                // Skipped: the next reconnect retries it
                logger.LogWarning(ex, "⚠️ Cannot rejoin SignalR group after reconnect: {ConversationId}", conversationId);
            }
        }
    }

    /// <summary>
    /// Disposes the SignalR connection resources.
    /// Called by the container when the visitor's circuit closes, after the page's own <see cref="StopAsync"/>.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        // Ends a first connection attempt that would otherwise outlive the circuit
        startCancellation?.Cancel();

        // Reached with a live connection only when the page never stopped it
        if (hubConnection != null)
            await hubConnection.DisposeAsync();

        // Still set only when the page never stopped the connection, the one path that would leave it undisposed
        startCancellation?.Dispose();
        startCancellation = null;

        // Unsealed like every default service, so a derived service's finalizer would find nothing left to release
        GC.SuppressFinalize(this);
    }
}
