using Microsoft.AspNetCore.SignalR.Client;

namespace Morgana.Web.Services;

public class MorganaSignalRService : IAsyncDisposable
{
    private HubConnection? _hubConnection;
    private readonly IConfiguration _configuration;

    public event Action<string, string, DateTime>? OnMessageReceived;
    public event Action<bool>? OnConnectionStateChanged;

    public bool IsConnected => _hubConnection?.State == HubConnectionState.Connected;

    public MorganaSignalRService(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public async Task StartAsync()
    {
        if (_hubConnection != null)
            return;

        string apiBaseUrl = _configuration["Morgana:BaseUrl"] ?? "https://localhost:5001";
        
        _hubConnection = new HubConnectionBuilder()
            .WithUrl($"{apiBaseUrl}/conversationHub")
            .WithAutomaticReconnect(new[] { 
                TimeSpan.Zero, 
                TimeSpan.FromSeconds(2), 
                TimeSpan.FromSeconds(5), 
                TimeSpan.FromSeconds(10) 
            })
            .Build();

        _hubConnection.On<MessageReceivedDto>("ReceiveMessage", (message) =>
        {
            OnMessageReceived?.Invoke(
                message.ConversationId,
                message.Text,
                message.Timestamp
            );
        });

        _hubConnection.Reconnecting += (error) =>
        {
            OnConnectionStateChanged?.Invoke(false);
            return Task.CompletedTask;
        };

        _hubConnection.Reconnected += (connectionId) =>
        {
            OnConnectionStateChanged?.Invoke(true);
            return Task.CompletedTask;
        };

        _hubConnection.Closed += (error) =>
        {
            OnConnectionStateChanged?.Invoke(false);
            return Task.CompletedTask;
        };

        await _hubConnection.StartAsync();
        OnConnectionStateChanged?.Invoke(true);
    }

    public async Task JoinConversation(string conversationId)
    {
        if (_hubConnection?.State == HubConnectionState.Connected)
        {
            await _hubConnection.InvokeAsync("JoinConversation", conversationId);
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
        if (_hubConnection != null)
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

    private class MessageReceivedDto
    {
        public string ConversationId { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
        public string? ErrorReason { get; set; }
    }
}