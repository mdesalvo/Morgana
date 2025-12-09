using Microsoft.AspNetCore.SignalR.Client;
using Morgana.Web.Messages;

namespace Morgana.Web.Services;

public class MorganaSignalRService : IAsyncDisposable
{
    private HubConnection? _hubConnection;
    private readonly IConfiguration _configuration;

    public event Action<string, string, string, DateTime>? OnMessageReceived;
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
            OnMessageReceived?.Invoke(
                message.ConversationId,
                message.UserId,
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

    public async Task JoinConversation(string conversationId, string userId)
    {
        if (_hubConnection?.State == HubConnectionState.Connected)
        {
            await _hubConnection.InvokeAsync("JoinConversation", conversationId, userId);
        }
    }

    public async Task LeaveConversation(string conversationId, string userId)
    {
        if (_hubConnection?.State == HubConnectionState.Connected)
        {
            await _hubConnection.InvokeAsync("LeaveConversation", conversationId, userId);
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