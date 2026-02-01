using Microsoft.AspNetCore.SignalR.Client;

namespace Cauldron.Services;

/// <summary>
/// Service for managing SignalR connection to Morgana backend.
/// Handles connection lifecycle, group membership, and strongly-typed message reception.
/// </summary>
/// <remarks>
/// <para><strong>Architecture:</strong></para>
/// <para>This service abstracts SignalR complexity from Blazor components.
/// Components subscribe to events rather than dealing with SignalR directly.</para>
/// <para><strong>Connection Management:</strong></para>
/// <list type="bullet">
/// <item>Automatic reconnection with exponential backoff</item>
/// <item>Connection state tracking and event notification</item>
/// <item>Graceful handling of network interruptions</item>
/// </list>
/// </remarks>
public class MorganaSignalRService : IAsyncDisposable
{
    private readonly IConfiguration configuration;
    private HubConnection? hubConnection;
    private readonly ILogger logger;

    /// <summary>
    /// Event raised when a message is received from the backend via SignalR.
    /// Subscribers receive a strongly-typed SignalRMessage DTO.
    /// </summary>
    /// <remarks>
    /// <para><strong>Simplified Signature:</strong></para>
    /// <para>Previously: 6+ individual parameters (conversationId, text, timestamp, ...)</para>
    /// <para>Now: Single SignalRMessage DTO with all fields</para>
    /// <para>This improves type safety, maintainability, and extensibility.</para>
    /// </remarks>
    public event Func<SignalRMessage, Task>? OnMessageReceived;

    /// <summary>
    /// Event raised when a streaming chunk is received from the backend via SignalR.
    /// Enables real-time progressive rendering of agent responses as they are generated.
    /// </summary>
    /// <remarks>
    /// <para><strong>Streaming Protocol:</strong></para>
    /// <para>Chunks arrive via "ReceiveStreamChunk" event during agent response generation.
    /// Each chunk contains partial text that should be appended to the current message being displayed.</para>
    /// <para><strong>Usage Pattern:</strong></para>
    /// <code>
    /// signalRService.OnStreamChunkReceived += async (chunkText) => {
    ///     // Append chunk to currently streaming message in UI
    ///     currentStreamingMessage.Text += chunkText;
    ///     StateHasChanged();
    /// };
    /// </code>
    /// <para><strong>Completion:</strong></para>
    /// <para>When streaming finishes, a complete message arrives via OnMessageReceived with full metadata
    /// (quick replies, agent completion status, etc.).</para>
    /// </remarks>
    public event Func<string, Task>? OnStreamChunkReceived;

    /// <summary>
    /// Event raised when SignalR connection state changes.
    /// </summary>
    /// <remarks>
    /// Subscribers receive true when connected, false when disconnected.
    /// Use this to update UI connection indicators.
    /// </remarks>
    public event Action<bool>? OnConnectionStateChanged;

    /// <summary>
    /// Gets a value indicating whether the SignalR connection is currently active.
    /// </summary>
    public bool IsConnected => hubConnection?.State == HubConnectionState.Connected;

    public MorganaSignalRService(IConfiguration configuration, ILogger logger)
    {
        this.configuration = configuration;
        this.logger = logger;
    }

    /// <summary>
    /// Starts the SignalR connection and subscribes to backend events.
    /// </summary>
    /// <returns>Task representing the async start operation</returns>
    /// <remarks>
    /// <para><strong>Connection Configuration:</strong></para>
    /// <list type="bullet">
    /// <item>Automatic reconnection with delays: 0s, 2s, 10s, 30s</item>
    /// <item>Hub endpoint from configuration: "{Morgana:BaseUrl}/conversationHub"</item>
    /// <item>WebSocket preferred, Server-Sent Events fallback</item>
    /// </list>
    /// <para><strong>Event Subscription:</strong></para>
    /// <para>Subscribes to "ReceiveMessage" event with strongly-typed SignalRMessage DTO.
    /// The DTO is automatically deserialized from JSON by SignalR client.</para>
    /// </remarks>
    public async Task StartAsync()
    {
        // Idempotent: skip if already connected
        if (hubConnection != null)
            return;

        string apiBaseUrl = configuration["Morgana:BaseUrl"]!;

        // Build SignalR hub connection with automatic reconnect
        hubConnection = new HubConnectionBuilder()
            .WithUrl($"{apiBaseUrl}/conversationHub")
            .WithAutomaticReconnect(
            [
                TimeSpan.Zero,           // Attempt 1: immediate
                TimeSpan.FromSeconds(2), // Attempt 2: 2 seconds
                TimeSpan.FromSeconds(5), // Attempt 3: 5 seconds
                TimeSpan.FromSeconds(10) // Attempt 4+: 10 seconds
            ])
            .Build();

        // Subscribe to ReceiveMessage
        hubConnection.On<SignalRMessage>("ReceiveMessage", async (message) =>
        {
            logger.LogInformation(
                $"📩 SignalR message received: {message.AgentName} -> " +
                $"{message.ConversationId} (type: {message.MessageType}, completed: {message.AgentCompleted})");

            // Invoke event with DTO (no parameter unpacking needed)
            await (OnMessageReceived?.Invoke(message) ?? Task.CompletedTask);
        });

        // Subscribe to ReceiveStreamChunk for progressive response rendering
        hubConnection.On<string>("ReceiveStreamChunk", async (chunkText) =>
        {
            await (OnStreamChunkReceived?.Invoke(chunkText) ?? Task.CompletedTask);
        });

        // Subscribe to connection state changes
        hubConnection.Closed += async (error) =>
        {
            logger.LogWarning($"❌ SignalR disconnected: {error?.Message ?? "No error"}");
            OnConnectionStateChanged?.Invoke(false);
            await Task.CompletedTask;
        };
        hubConnection.Reconnecting += async (error) =>
        {
            logger.LogInformation($"🔄 SignalR reconnecting: {error?.Message ?? "No error"}");
            OnConnectionStateChanged?.Invoke(false);
            await Task.CompletedTask;
        };
        hubConnection.Reconnected += async (connectionId) =>
        {
            logger.LogInformation($"✅ SignalR reconnected: {connectionId}");
            OnConnectionStateChanged?.Invoke(true);
            await Task.CompletedTask;
        };

        // Start connection
        await hubConnection.StartAsync();
        logger.LogInformation("✅ SignalR connected and listening for messages");

        // Notify subscribers
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
            hubConnection = null;

            OnConnectionStateChanged?.Invoke(false);
            logger.LogWarning("🛑 SignalR connection stopped");
        }
    }

    /// <summary>
    /// Joins a conversation group to start receiving messages for that conversation.
    /// </summary>
    /// <param name="conversationId">Unique identifier of the conversation to join</param>
    /// <returns>Task representing the async join operation</returns>
    /// <remarks>
    /// Must be called after starting a conversation via REST API.
    /// All messages for this conversation will be delivered via OnMessageReceived event.
    /// </remarks>
    public async Task JoinConversation(string conversationId)
    {
        if (hubConnection?.State == HubConnectionState.Connected)
        {
            await hubConnection.InvokeAsync("JoinConversation", conversationId);
            logger.LogInformation($"✅ Joined SignalR group: {conversationId}");
        }
        else
        {
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
        if (hubConnection?.State == HubConnectionState.Connected)
        {
            await hubConnection.InvokeAsync("LeaveConversation", conversationId);
            logger.LogInformation($"👋 Left SignalR group: {conversationId}");
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