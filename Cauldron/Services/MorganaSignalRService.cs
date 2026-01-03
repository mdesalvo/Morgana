using Microsoft.AspNetCore.SignalR.Client;
using Cauldron.Messages;

namespace Cauldron.Services;

public class MorganaSignalRService : IAsyncDisposable
{
    private HubConnection? _hubConnection;
    private readonly IConfiguration _configuration;
    private readonly ILogger<MorganaSignalRService> _logger;

    public event Action<string, string, DateTime, List<QuickReply>?, string?, bool>? OnMessageReceived;
    public event Action<bool>? OnConnectionStateChanged;

    public bool IsConnected => _hubConnection?.State == HubConnectionState.Connected;

    public MorganaSignalRService(IConfiguration configuration, ILogger<MorganaSignalRService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task StartAsync()
    {
        if (_hubConnection != null)
            return;

        string apiBaseUrl = _configuration["Morgana:BaseUrl"]!;

        _hubConnection = new HubConnectionBuilder()
            .WithUrl($"{apiBaseUrl}/conversationHub")
            .WithAutomaticReconnect(
            [
                TimeSpan.Zero,
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(10)
            ])
            .Build();

        _hubConnection.On<MessageReceived>("ReceiveMessage", (message) =>
        {
            _logger.LogInformation($"📩 Message received - Type: {message.MessageType}, QuickReplies: {message.QuickReplies?.Count ?? 0}, Agent: {message.AgentName ?? "Morgana"}, Completed: {message.AgentCompleted}");
            
            OnMessageReceived?.Invoke(
                message.ConversationId,
                message.Text,
                message.Timestamp,
                message.QuickReplies,
                message.AgentName ?? "Morgana",
                message.AgentCompleted
            );
        });

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

        await _hubConnection.StartAsync();
        OnConnectionStateChanged?.Invoke(true);
        _logger.LogInformation("SignalR started successfully");
    }

    public async Task JoinConversation(string conversationId)
    {
        if (_hubConnection?.State == HubConnectionState.Connected)
        {
            await _hubConnection.InvokeAsync("JoinConversation", conversationId);
            _logger.LogInformation($"Joined conversation: {conversationId}");
        }
    }

    public async Task LeaveConversation(string conversationId)
    {
        if (_hubConnection?.State == HubConnectionState.Connected)
        {
            await _hubConnection.InvokeAsync("LeaveConversation", conversationId);
        }
    }

    public async Task StopAsync()
    {
        if (_hubConnection?.ConnectionId != null)
        {
            await _hubConnection.StopAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_hubConnection != null)
        {
            await _hubConnection.DisposeAsync();
        }
    }
}