using Cauldron.Interfaces;
using Cauldron.Messages;

namespace Cauldron.Services;

/// <summary>
/// Manages conversation lifecycle: start, resume, clear.
/// Coordinates between HTTP API, SignalR groups, and local storage.
/// </summary>
public class ConversationLifecycleService : IConversationLifecycleService
{
    private readonly HttpClient _http;
    private readonly SignalRService _signalR;
    private readonly IConversationStorageService _storage;
    private readonly IConversationHistoryService _history;
    private readonly IChatStateService _chatStateService;
    private readonly ILogger _logger;

    public ConversationLifecycleService(
        HttpClient http,
        SignalRService signalR,
        IConversationStorageService storage,
        IConversationHistoryService history,
        IChatStateService chatState,
        ILogger logger)
    {
        _http = http;
        _signalR = signalR;
        _storage = storage;
        _history = history;
        _chatStateService = chatState;
        _logger = logger;
    }

    /// <summary>
    /// Starts a new conversation with Morgana backend.
    /// </summary>
    /// <returns>True if conversation started successfully.</returns>
    public async Task<bool> StartConversationAsync()
    {
        try
        {
            _logger.LogInformation("Starting new conversation...");

            HttpResponseMessage response = await _http.PostAsJsonAsync("/api/morgana/conversation/start", new
            {
                conversationId = Guid.NewGuid().ToString("N")
            });

            if (response.IsSuccessStatusCode)
            {
                ConversationStartResponse? result = await response.Content
                    .ReadFromJsonAsync<ConversationStartResponse>();

                _chatStateService.ConversationId = result?.ConversationId ?? string.Empty;

                await _signalR.JoinConversation(_chatStateService.ConversationId);
                await _storage.SaveConversationIdAsync(_chatStateService.ConversationId);

                _logger.LogInformation("Conversation started: {ConversationId}", _chatStateService.ConversationId);
                return true;
            }

            string errorContent = await response.Content.ReadAsStringAsync();
            _logger.LogError("Failed to start conversation: {StatusCode} - {Error}", response.StatusCode, errorContent);
            _chatStateService.AddErrorBanner($"Failed to start conversation: {response.StatusCode}", "conversation_start_http_error", 12);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "StartConversation exception");
            _chatStateService.AddErrorBanner($"Connection error: {ex.Message}", "conversation_start_exception", 20);
            return false;
        }
    }

    /// <summary>
    /// Resumes an existing conversation from storage.
    /// Falls back to StartConversationAsync on any failure.
    /// </summary>
    /// <returns>True if conversation was resumed or a new one started successfully.</returns>
    public async Task<bool> ResumeConversationAsync(string savedConversationId)
    {
        try
        {
            _logger.LogInformation("Attempting to resume conversation {ConversationId}", savedConversationId);

            HttpResponseMessage response = await _http.PostAsync(
                $"/api/morgana/conversation/{savedConversationId}/resume", null);

            if (response.IsSuccessStatusCode)
            {
                ConversationResumeResponse? result = await response.Content
                    .ReadFromJsonAsync<ConversationResumeResponse>();

                _chatStateService.ConversationId = result?.ConversationId ?? savedConversationId;

                if (string.IsNullOrEmpty(result?.ActiveAgent)
                    || string.Equals(result.ActiveAgent, "Morgana", StringComparison.OrdinalIgnoreCase))
                {
                    _chatStateService.CurrentAgentName = "Morgana";
                }
                else
                {
                    _chatStateService.CurrentAgentName = $"Morgana ({char.ToUpper(result.ActiveAgent[0]) + result.ActiveAgent[1..]})";
                }

                await _signalR.JoinConversation(_chatStateService.ConversationId);

                return await LoadHistoryAsync();
            }

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                _logger.LogWarning("Conversation {ConversationId} not found, starting fresh", savedConversationId);
            else
                _logger.LogError("Resume error {StatusCode}", response.StatusCode);

            return await FallbackToNewConversationAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ResumeConversation exception");
            return await FallbackToNewConversationAsync();
        }
    }

    /// <summary>
    /// Clears the saved conversation from storage.
    /// </summary>
    public async Task ClearConversationAsync()
    {
        _logger.LogInformation("Clearing conversation from storage");
        await _storage.ClearConversationIdAsync();
    }

    /// <summary>
    /// Checks storage for an existing conversation ID.
    /// </summary>
    public async Task<string?> GetSavedConversationIdAsync()
    {
        return await _storage.GetConversationIdAsync();
    }

    /// <summary>
    /// Sends a user message to the Morgana backend.
    /// </summary>
    /// <returns>True if the message was sent successfully.</returns>
    public async Task<bool> SendMessageAsync(string text)
    {
        try
        {
            HttpResponseMessage response = await _http.PostAsJsonAsync(
                $"/api/morgana/conversation/{_chatStateService.ConversationId}/message", new
                {
                    conversationId = _chatStateService.ConversationId,
                    text
                });

            _logger.LogInformation("Message sent, response status: {StatusCode}", response.StatusCode);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("SendMessage failed: {StatusCode}", response.StatusCode);
                _chatStateService.RemoveTypingIndicator();
                _chatStateService.AddChatError($"Message not sent: {response.StatusCode}. Please try again.", "send_message_http_error");
                _chatStateService.IsSending = false;
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SendMessage exception");
            _chatStateService.RemoveTypingIndicator();
            _chatStateService.AddChatError(
                $"Connection error: {ex.Message}. Please try again.",
                "send_message_exception");
            _chatStateService.IsSending = false;
            return false;
        }
    }

    // =========================================================================
    // PRIVATE HELPERS
    // =========================================================================

    private async Task<bool> LoadHistoryAsync()
    {
        try
        {
            ConversationHistoryResponse? history = await _history.GetHistoryAsync(_chatStateService.ConversationId);

            if (history?.Messages is not { Length: > 0 })
            {
                _logger.LogWarning("No history found for conversation {ConversationId}", _chatStateService.ConversationId);
                return await FallbackToNewConversationAsync();
            }

            _logger.LogInformation("Retrieved {Count} messages from history", history.Messages.Length);

            for (int i = 0; i < history.Messages.Length; i++)
            {
                // Inject transient agent-turn-boundary hints
                if (string.Equals(history.Messages[i].Role, "user", StringComparison.OrdinalIgnoreCase))
                {
                    bool isPrecededByAssistant = i > 0
                        && string.Equals(history.Messages[i - 1].Role, "assistant", StringComparison.OrdinalIgnoreCase);
                    bool isFollowedByAssistant = i + 1 < history.Messages.Length
                        && string.Equals(history.Messages[i + 1].Role, "assistant", StringComparison.OrdinalIgnoreCase);
                    bool isTurnBoundary = isPrecededByAssistant
                        && isFollowedByAssistant
                        && !string.Equals(history.Messages[i - 1].AgentName, history.Messages[i + 1].AgentName, StringComparison.OrdinalIgnoreCase);

                    if (isTurnBoundary)
                    {
                        _chatStateService.ChatMessages.Add(new ChatMessage
                        {
                            ConversationId = history.Messages[i].ConversationId,
                            Text = _chatStateService.GetCompletionMessage(history.Messages[i - 1].AgentName),
                            Role = "assistant",
                            Timestamp = history.Messages[i].Timestamp.AddMilliseconds(-5),
                            AgentName = "Morgana",
                            AgentCompleted = true,
                            Type = MessageType.Presentation
                        });
                    }
                }

                _chatStateService.ChatMessages.Add(history.Messages[i]);
            }

            // Detect trailing agent boundary
            ChatMessage lastMsg = history.Messages.Last();
            if (_chatStateService.IsSpecializedAgent(lastMsg.AgentName)
                && !_chatStateService.IsSpecializedAgent(_chatStateService.CurrentAgentName))
            {
                _chatStateService.ChatMessages.Add(new ChatMessage
                {
                    ConversationId = lastMsg.ConversationId,
                    Text = _chatStateService.GetCompletionMessage(lastMsg.AgentName),
                    Role = "assistant",
                    Timestamp = lastMsg.Timestamp.AddMilliseconds(-5),
                    AgentName = "Morgana",
                    AgentCompleted = true,
                    Type = MessageType.Presentation
                });
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load history");
            return await FallbackToNewConversationAsync();
        }
    }

    private async Task<bool> FallbackToNewConversationAsync()
    {
        await _storage.ClearConversationIdAsync();
        return await StartConversationAsync();
    }
}
