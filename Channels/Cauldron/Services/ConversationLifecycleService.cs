using Cauldron.Interfaces;
using Cauldron.Messages;
using Morgana.Contracts;

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
        // Cauldron is the reference channel: announces itself by name and declares
        // full capabilities at handshake, so Morgana persists the metadata and stops
        // relying on hard-coded defaults.
        StartConversationRequest request = new(
            ConversationId: Guid.NewGuid().ToString("N"),
            ChannelMetadata: CauldronChannelMetadata.Profile);

        // Scopes every log line below to this conversation id, so a single id lets an
        // operator pull the whole start attempt out of the log stream, failures included.
        using IDisposable? scope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["ConversationId"] = request.ConversationId
        });

        try
        {
            _logger.LogInformation("Starting new conversation...");

            HttpResponseMessage response = await _http.PostAsJsonAsync(
                "/api/morgana/conversation/start", request);

            if (response.IsSuccessStatusCode)
            {
                StartConversationResponse? result = await response.Content
                    .ReadFromJsonAsync<StartConversationResponse>();

                // The server's id wins over the one just minted: it is what every later call
                // and every SignalR group is keyed on.
                _chatStateService.ConversationId = result?.ConversationId ?? string.Empty;

                // Join before the presentation message is generated, or it arrives with nobody
                // listening — the greeting is pushed, never polled.
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
        // Scopes every log line below to this conversation id, so a single id lets an
        // operator pull the whole start attempt out of the log stream, failures included.
        using IDisposable? scope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["ConversationId"] = savedConversationId
        });

        try
        {
            _logger.LogInformation("Attempting to resume conversation {ConversationId}", savedConversationId);

            HttpResponseMessage response = await _http.PostAsync(
                $"/api/morgana/conversation/{savedConversationId}/resume", null);

            if (response.IsSuccessStatusCode)
            {
                ResumeConversationResponse? result = await response.Content
                    .ReadFromJsonAsync<ResumeConversationResponse>();

                _chatStateService.ConversationId = result?.ConversationId ?? savedConversationId;

                // Rehydrate the dust gauge from the resumed conversation's budget so the
                // widget reflects real residual dust immediately, not a pristine bar that
                // only corrects itself after the first post-resume turn. Null leaves the
                // indicator hidden (dust limiting disabled on Morgana).
                _chatStateService.DustLevel = result?.DustLevel;

                // The resumed conversation is already dust-dead: re-surface the canonical
                // terminal banner up front (same ErrorReason as the live lockout, so the
                // input lock, the guaranteed New Conversation button and the non-fading
                // purple styling all engage) instead of letting the user discover the
                // wall by firing a message that is instantly rejected.
                if (!string.IsNullOrEmpty(result?.DustExhaustedMessage))
                    _chatStateService.AddErrorBanner(result.DustExhaustedMessage, "dust_budget_exhausted");

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

            // 404 is the ordinary case of a stale browser: the saved id outlived the server's
            // knowledge of it. Anything else is a real failure, but both recover the same way.
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
    /// Ends the current conversation server-side so Morgana can tear down its actor tree.
    /// </summary>
    public async Task EndConversationAsync()
    {
        // Snapshot the id before anything else touches the state, so the whole teardown targets
        // one conversation even though the caller clears the state right after.
        string conversationId = _chatStateService.ConversationId;

        // Nothing to end when no conversation was ever established (first load, or a start that failed)
        if (string.IsNullOrEmpty(conversationId))
            return;

        // Scopes every log line below to this conversation id, so a single id lets an
        // operator pull the whole start attempt out of the log stream, failures included.
        using IDisposable? scope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["ConversationId"] = conversationId
        });

        try
        {
            // Leave the SignalR group first, so anything still in flight for this conversation
            // does not land on a circuit that has already walked away from it.
            await _signalR.LeaveConversation(conversationId);

            // Tell Morgana the conversation is over: the manager stops the supervisor and the
            // guard/classifier/router/agent subtree underneath it, releasing their sessions.
            // The persisted history is untouched — this frees actors, it does not delete data.
            await _http.PostAsync($"/api/morgana/conversation/{conversationId}/end", content: null);

            _logger.LogInformation("Conversation ended: {ConversationId}", conversationId);
        }
        catch (Exception ex)
        {
            // Best-effort cleanup: the user is leaving this conversation either way, and the next
            // load starts a fresh one. A failure here costs an actor tree on the server, not the
            // user's ability to move on, so it is logged and swallowed rather than surfaced.
            _logger.LogWarning(ex, "EndConversation failed for {ConversationId}", conversationId);
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
        // Scopes every log line below to this conversation id, so a single id lets an
        // operator pull the whole start attempt out of the log stream, failures included.
        using IDisposable? scope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["ConversationId"] = _chatStateService.ConversationId
        });

        try
        {
            SendMessageRequest request = new(
                ConversationId: _chatStateService.ConversationId,
                Text: text);

            HttpResponseMessage response = await _http.PostAsJsonAsync(
                $"/api/morgana/conversation/{_chatStateService.ConversationId}/message", request);

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

            // Walked in order so the synthetic handover lines can be woven in at the right spot
            for (int i = 0; i < history.Messages.Length; i++)
            {
                // Handover lines are never persisted, so they are reconstructed here by spotting
                // a user turn that sits between two different agents.
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
                        // Backdated just before the user message it precedes, so it reads as the
                        // specialist signing off rather than answering.
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

                _chatStateService.ChatMessages.Add(MapToChatMessage(history.Messages[i]));
            }

            // The loop above only catches handovers followed by another turn. A conversation that
            // ended while a specialist was active needs its closing line added here.
            MorganaChatMessage lastMsg = history.Messages.Last();
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

    /// <summary>
    /// Projects a history message from the wire contract onto the mutable UI model.
    /// </summary>
    /// <remarks>
    /// The two models are deliberately distinct: <see cref="MorganaChatMessage"/> is what Morgana
    /// persisted, <see cref="ChatMessage"/> carries UI-only state the server knows nothing about
    /// (typing indicator, streaming flag, selected quick reply). Mapping the message type through
    /// an exhaustive switch means a new value added server-side surfaces here rather than being
    /// silently coerced into the wrong UI styling.
    /// </remarks>
    private static ChatMessage MapToChatMessage(MorganaChatMessage message) =>
        new()
        {
            ConversationId = message.ConversationId,
            Text = message.Text,
            Timestamp = message.Timestamp,
            Type = message.Type switch
            {
                ChatMessageType.User => MessageType.User,
                ChatMessageType.Assistant => MessageType.Assistant,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(message), message.Type, "Unmapped history message type.")
            },
            AgentName = message.AgentName,
            AgentCompleted = message.AgentCompleted,
            QuickReplies = message.QuickReplies,
            RichCard = message.RichCard,
            IsLastHistoryMessage = message.IsLastHistoryMessage
        };

    private async Task<bool> FallbackToNewConversationAsync()
    {
        // Drop the unusable id first: leaving it would make the next page load retry the same
        // dead conversation and fall through to here again.
        await _storage.ClearConversationIdAsync();
        return await StartConversationAsync();
    }
}