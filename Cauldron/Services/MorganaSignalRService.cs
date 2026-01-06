using Microsoft.AspNetCore.SignalR.Client;
using Cauldron.Messages;

namespace Cauldron.Services;

/// <summary>
/// SignalR client service for real-time communication with Morgana backend.
/// Manages WebSocket connection, automatic reconnection, and message routing to UI components.
/// </summary>
/// <remarks>
/// <para><strong>Purpose:</strong></para>
/// <para>This service provides a managed SignalR connection to the Morgana ConversationHub,
/// enabling real-time bi-directional communication for chat interactions. It handles connection
/// lifecycle, automatic reconnection on failure, and event-based message delivery to subscribers.</para>
/// <para><strong>Connection Flow:</strong></para>
/// <code>
/// 1. Application startup
/// 2. MorganaSignalRService.StartAsync() called
/// 3. HubConnection created with automatic reconnect policy
/// 4. Connection established to /conversationHub endpoint
/// 5. Event handlers registered for ReceiveMessage, Reconnecting, Reconnected, Closed
/// 6. OnConnectionStateChanged event fired (true)
/// 7. UI subscribes to OnMessageReceived event
/// 8. Messages flow: Backend → SignalR → Service → UI
/// </code>
/// <para><strong>Reconnection Strategy:</strong></para>
/// <list type="bullet">
/// <item>Attempt 1: Immediate reconnection (0 seconds)</item>
/// <item>Attempt 2: Wait 2 seconds</item>
/// <item>Attempt 3: Wait 5 seconds</item>
/// <item>Attempt 4+: Wait 10 seconds</item>
/// </list>
/// <para><strong>Usage in Index.razor:</strong></para>
/// <code>
/// @inject MorganaSignalRService SignalRService
/// 
/// protected override async Task OnInitializedAsync()
/// {
///     await SignalRService.StartAsync();
///     
///     SignalRService.OnMessageReceived += async (conversationId, text, timestamp, quickReplies, agentName, agentCompleted) =>
///     {
///         await HandleMessageReceived(conversationId, text, timestamp, quickReplies, agentName, agentCompleted);
///     };
///     
///     SignalRService.OnConnectionStateChanged += (connected) =>
///     {
///         isConnected = connected;
///         InvokeAsync(StateHasChanged);
///     };
/// }
/// </code>
/// </remarks>
public class MorganaSignalRService : IAsyncDisposable
{
    private HubConnection? _hubConnection;
    private readonly IConfiguration _configuration;
    private readonly ILogger<MorganaSignalRService> _logger;

    /// <summary>
    /// Event fired when a message is received from the backend via SignalR.
    /// Provides all message details including quick replies, agent name, and completion status.
    /// </summary>
    /// <remarks>
    /// <para><strong>Event Parameters:</strong></para>
    /// <list type="bullet">
    /// <item><term>conversationId</term><description>Unique conversation identifier</description></item>
    /// <item><term>text</term><description>Message text from agent</description></item>
    /// <item><term>timestamp</term><description>Message timestamp</description></item>
    /// <item><term>quickReplies</term><description>Optional list of quick reply buttons</description></item>
    /// <item><term>agentName</term><description>Name of responding agent (e.g., "Morgana", "Billing")</description></item>
    /// <item><term>agentCompleted</term><description>True if agent has completed its task (return to idle)</description></item>
    /// </list>
    /// </remarks>
    public event Action<string, string, DateTime, List<QuickReply>?, string?, bool>? OnMessageReceived;
    
    /// <summary>
    /// Event fired when SignalR connection state changes.
    /// Provides connection status for UI indicator updates.
    /// </summary>
    /// <remarks>
    /// <para><strong>Connection States:</strong></para>
    /// <list type="bullet">
    /// <item>true: Connected and ready for messages</item>
    /// <item>false: Disconnected or reconnecting</item>
    /// </list>
    /// </remarks>
    public event Action<bool>? OnConnectionStateChanged;

    /// <summary>
    /// Gets a value indicating whether the SignalR connection is currently established.
    /// </summary>
    public bool IsConnected => _hubConnection?.State == HubConnectionState.Connected;

    /// <summary>
    /// Initializes a new instance of MorganaSignalRService.
    /// </summary>
    /// <param name="configuration">Application configuration for Morgana base URL</param>
    /// <param name="logger">Logger instance for connection diagnostics</param>
    public MorganaSignalRService(IConfiguration configuration, ILogger<MorganaSignalRService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Starts the SignalR connection to the Morgana backend.
    /// Configures automatic reconnection and registers event handlers.
    /// </summary>
    /// <returns>Task representing the async connection operation</returns>
    /// <remarks>
    /// <para><strong>Connection Configuration:</strong></para>
    /// <list type="bullet">
    /// <item>Endpoint: {Morgana:BaseUrl}/conversationHub</item>
    /// <item>Automatic reconnect with exponential backoff</item>
    /// <item>Event handlers for ReceiveMessage, Reconnecting, Reconnected, Closed</item>
    /// </list>
    /// <para><strong>Idempotency:</strong></para>
    /// <para>If already connected, this method returns immediately without creating a new connection.
    /// Safe to call multiple times.</para>
    /// <para><strong>Logging:</strong></para>
    /// <para>Logs all messages received with type, quick reply count, agent name, and completion status.</para>
    /// </remarks>
    public async Task StartAsync()
    {
        // Idempotent: skip if already connected
        if (_hubConnection != null)
            return;

        string apiBaseUrl = _configuration["Morgana:BaseUrl"]!;

        // Build SignalR hub connection with automatic reconnect
        _hubConnection = new HubConnectionBuilder()
            .WithUrl($"{apiBaseUrl}/conversationHub")
            .WithAutomaticReconnect(
            [
                TimeSpan.Zero,           // Attempt 1: immediate
                TimeSpan.FromSeconds(2), // Attempt 2: 2 seconds
                TimeSpan.FromSeconds(5), // Attempt 3: 5 seconds
                TimeSpan.FromSeconds(10) // Attempt 4+: 10 seconds
            ])
            .Build();

        // Register handler for incoming messages from backend
        _hubConnection.On<MessageReceived>("ReceiveMessage", (message) =>
        {
            _logger.LogInformation($"📩 Message received - Type: {message.MessageType}, QuickReplies: {message.QuickReplies?.Count ?? 0}, Agent: {message.AgentName ?? "Morgana"}, Completed: {message.AgentCompleted}");
            
            // Fire event to notify subscribers (typically Index.razor)
            OnMessageReceived?.Invoke(
                message.ConversationId,
                message.Text,
                message.Timestamp,
                message.QuickReplies,
                message.AgentName ?? "Morgana",
                message.AgentCompleted
            );
        });

        // Register reconnection event handlers
        _hubConnection.Reconnecting += (error) =>
        {
            _logger.LogWarning("SignalR reconnecting...");
            OnConnectionStateChanged?.Invoke(false);
            return Task.CompletedTask;
        };
        _hubConnection.Reconnected += (connectionId) =>
        {
            _logger.LogInformation($"SignalR reconnected: {connectionId}");
            OnConnectionStateChanged?.Invoke(true);
            return Task.CompletedTask;
        };
        _hubConnection.Closed += (error) =>
        {
            _logger.LogError($"SignalR closed: {error?.Message}");
            OnConnectionStateChanged?.Invoke(false);
            return Task.CompletedTask;
        };

        // Start connection
        await _hubConnection.StartAsync();
        OnConnectionStateChanged?.Invoke(true);
        _logger.LogInformation("SignalR started successfully");
    }

    /// <summary>
    /// Joins a specific conversation group on the SignalR hub.
    /// Required for receiving messages for this conversation.
    /// </summary>
    /// <param name="conversationId">Unique conversation identifier</param>
    /// <returns>Task representing the async join operation</returns>
    /// <remarks>
    /// <para><strong>Group-Based Routing:</strong></para>
    /// <para>SignalR uses groups to route messages to specific conversations. After starting a conversation
    /// via REST API, the client must join the conversation group to receive messages.</para>
    /// <para><strong>Typical Flow:</strong></para>
    /// <code>
    /// 1. POST /api/conversation/start → Returns conversationId
    /// 2. SignalR.JoinConversation(conversationId) → Joins group
    /// 3. Backend sends messages to group → Client receives via OnMessageReceived
    /// </code>
    /// </remarks>
    public async Task JoinConversation(string conversationId)
    {
        if (_hubConnection?.State == HubConnectionState.Connected)
        {
            await _hubConnection.InvokeAsync("JoinConversation", conversationId);
            _logger.LogInformation($"Joined conversation: {conversationId}");
        }
    }

    /// <summary>
    /// Leaves a specific conversation group on the SignalR hub.
    /// Optional cleanup when ending a conversation.
    /// </summary>
    /// <param name="conversationId">Unique conversation identifier to leave</param>
    /// <returns>Task representing the async leave operation</returns>
    public async Task LeaveConversation(string conversationId)
    {
        if (_hubConnection?.State == HubConnectionState.Connected)
        {
            await _hubConnection.InvokeAsync("LeaveConversation", conversationId);
        }
    }

    /// <summary>
    /// Stops the SignalR connection gracefully.
    /// </summary>
    /// <returns>Task representing the async stop operation</returns>
    public async Task StopAsync()
    {
        if (_hubConnection?.ConnectionId != null)
        {
            await _hubConnection.StopAsync();
        }
    }

    /// <summary>
    /// Disposes the SignalR connection resources.
    /// Called automatically when the service is disposed (application shutdown).
    /// </summary>
    /// <returns>ValueTask representing the async dispose operation</returns>
    public async ValueTask DisposeAsync()
    {
        if (_hubConnection != null)
        {
            await _hubConnection.DisposeAsync();
        }
    }
}