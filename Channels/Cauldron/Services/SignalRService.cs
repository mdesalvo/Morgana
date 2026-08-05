using Cauldron.Handlers;
using Morgana.Contracts;
using Microsoft.AspNetCore.SignalR.Client;

namespace Cauldron.Services;

/// <summary>
/// SignalR client for the Morgana backend: owns the hub connection, its reconnection policy and
/// group membership, and republishes what arrives as plain events. This is the only place in
/// Cauldron that touches SignalR — everything else subscribes.
/// </summary>
public class SignalRService : IAsyncDisposable
{
    private readonly IConfiguration configuration;
    private readonly MorganaAuthHandler authHandler;
    private HubConnection? hubConnection;
    private readonly ILogger logger;

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

    public SignalRService(IConfiguration configuration, MorganaAuthHandler authHandler, ILogger logger)
    {
        this.configuration = configuration;
        this.authHandler = authHandler;
        this.logger = logger;
    }

    /// <summary>
    /// Opens the hub connection and wires up the backend event subscriptions.
    /// </summary>
    /// <returns>Task representing the async start operation</returns>
    public async Task StartAsync()
    {
        // Idempotent: a connection already exists, so there is nothing to build
        if (hubConnection != null)
            return;

        string apiBaseUrl = configuration["Cauldron:MorganaURL"]!;

        hubConnection = new HubConnectionBuilder()
            .WithUrl($"{apiBaseUrl}/morganaHub", options =>
            {
                // Re-issued on every (re)connect attempt: tokens are short-lived, so the callback
                // has to mint a fresh one rather than capture one at build time.
                options.AccessTokenProvider = () => Task.FromResult<string?>(authHandler.GenerateToken());
            })
            // Four attempts on a widening backoff, then the connection is given up as Closed
            .WithAutomaticReconnect(
            [
                TimeSpan.Zero,           // Attempt 1: immediate
                TimeSpan.FromSeconds(2), // Attempt 2: 2 seconds
                TimeSpan.FromSeconds(5), // Attempt 3: 5 seconds
                TimeSpan.FromSeconds(10) // Attempt 4+: 10 seconds
            ])
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

            await (OnMessageReceived?.Invoke(message) ?? Task.CompletedTask);
        });

        // Streaming chunks: forwarded raw and unlogged, they arrive one per token
        hubConnection.On<string>("ReceiveStreamChunk", async (chunkText) =>
        {
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

        // Reconnect succeeded. Note the connection id changes, so group membership does not
        // survive: callers relying on a conversation group must rejoin it.
        hubConnection.Reconnected += async (connectionId) =>
        {
            logger.LogInformation("✅ SignalR reconnected: {ConnectionId}", connectionId);
            OnConnectionStateChanged?.Invoke(true);
            await Task.CompletedTask;
        };

        await hubConnection.StartAsync();
        logger.LogInformation("✅ SignalR connected and listening for messages");

        // Announce the initial state, which subscribers have no other way to observe
        OnConnectionStateChanged?.Invoke(true);
        logger.LogInformation("SignalR started successfully");
    }

    /// <summary>
    /// Stops the SignalR connection gracefully.
    /// </summary>
    /// <returns>Task representing the async stop operation</returns>
    public async Task StopAsync()
    {
        if (hubConnection != null)
        {
            await hubConnection.StopAsync();
            await hubConnection.DisposeAsync();

            // Cleared so a later StartAsync builds a fresh connection instead of short-circuiting
            hubConnection = null;

            OnConnectionStateChanged?.Invoke(false);
            logger.LogWarning("🛑 SignalR connection stopped");
        }
    }

    /// <summary>
    /// Joins a conversation group, which is what makes this client start receiving that
    /// conversation's messages. Call it after the REST start/resume succeeds.
    /// </summary>
    /// <param name="conversationId">Unique identifier of the conversation to join</param>
    /// <returns>Task representing the async join operation</returns>
    public async Task JoinConversation(string conversationId)
    {
        if (hubConnection?.State == HubConnectionState.Connected)
        {
            await hubConnection.InvokeAsync("JoinConversation", conversationId);
            logger.LogInformation("✅ Joined SignalR group: {ConversationId}", conversationId);
        }
        else
        {
            // Not connected: the join is dropped rather than queued, so the caller ends up
            // subscribed to nothing and simply never sees this conversation's traffic.
            logger.LogWarning("⚠️ Cannot join conversation: SignalR not connected");
        }
    }

    /// <summary>
    /// Leaves a conversation group to stop receiving messages for that conversation.
    /// </summary>
    /// <param name="conversationId">Unique identifier of the conversation to leave</param>
    /// <returns>Task representing the async leave operation</returns>
    public async Task LeaveConversation(string conversationId)
    {
        // Nothing to leave when disconnected: the group membership died with the connection
        if (hubConnection?.State == HubConnectionState.Connected)
        {
            await hubConnection.InvokeAsync("LeaveConversation", conversationId);
            logger.LogInformation("👋 Left SignalR group: {ConversationId}", conversationId);
        }
    }

    /// <summary>
    /// Disposes the SignalR connection resources.
    /// Called automatically when the service is disposed (application shutdown).
    /// </summary>
    /// <returns>ValueTask representing the async dispose operation</returns>
    public async ValueTask DisposeAsync()
    {
        if (hubConnection != null)
        {
            await hubConnection.DisposeAsync();
        }
    }
}
