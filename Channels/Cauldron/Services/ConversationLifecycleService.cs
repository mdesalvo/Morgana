using Cauldron.Interfaces;
using Cauldron.Messages;
using Morgana.Contracts;

namespace Cauldron.Services;

/// <summary>
/// Starts, resumes and ends the visitor's conversation with Morgana, keeping the SignalR group, the stored id and the reply deadline in step.
/// One instance per circuit: the deadline is the turn in flight of this visitor alone.
/// </summary>
public class ConversationLifecycleService : IConversationLifecycleService, IDisposable
{
    /// <summary>Calls Morgana's conversation REST API with the channel's token.</summary>
    private readonly HttpClient _http;

    /// <summary>The hub connection, whose conversation group receives every push.</summary>
    private readonly SignalRService _signalR;

    /// <summary>The conversation id kept in the browser, which a returning visitor resumes.</summary>
    private readonly IConversationStorageService _storage;

    /// <summary>Morgana's record of the conversation, read on resume and to catch up missed replies.</summary>
    private readonly IConversationHistoryService _history;

    /// <summary>What the page shows: the messages, the composer's state and the banners.</summary>
    private readonly IChatStateService _chatStateService;

    /// <summary>The typewriter of the reply being streamed, dropped when a turn is abandoned.</summary>
    private readonly IStreamingService _streamingService;

    /// <summary>Logs each step of the lifecycle under the conversation's id.</summary>
    private readonly ILogger _logger;

    /// <summary>Counts down the silence on the turn in flight; null when nothing is being waited for.</summary>
    private Timer? _replyDeadline;

    /// <summary>Guards the deadline against a reply and a shutdown reaching it at the same moment.</summary>
    private readonly Lock _replyDeadlineLock = new();

    /// <inheritdoc />
    public event Action? OnReplyDeadlineExpired;

    /// <inheritdoc />
    public bool IsAwaitingReply => _chatStateService.IsSending || _chatStateService.HasTypingIndicator();

    /// <summary>Binds the lifecycle of one circuit to its services.</summary>
    public ConversationLifecycleService(
        HttpClient http,
        SignalRService signalR,
        IConversationStorageService storage,
        IConversationHistoryService history,
        IChatStateService chatState,
        IStreamingService streaming,
        ILogger logger)
    {
        _http = http;
        _signalR = signalR;
        _storage = storage;
        _history = history;
        _chatStateService = chatState;
        _streamingService = streaming;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<bool> StartConversationAsync()
    {
        // Cauldron announces itself by name and declares every capability, so Morgana adapts nothing of what it sends.
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

            // Joined before the conversation exists, not after it starts. The presentation is pushed
            // the instant Morgana has a conversation and a SignalR message sent to a group with no
            // members is discarded without a trace — so a join issued after the start response is a
            // race, lost every time the greeting is cached or written without a model. Joining first
            // removes the race: the id is minted here and a group is joinable before anything is keyed on it.
            await _signalR.JoinConversation(request.ConversationId);

            // Morgana settles the handshake and creates the conversation's actors.
            HttpResponseMessage response = await _http.PostAsJsonAsync(
                "/api/morgana/conversation/start", request);

            // The conversation exists: its id is kept for the visitor's return.
            if (response.IsSuccessStatusCode)
            {
                StartConversationResponse? result = await response.Content
                    .ReadFromJsonAsync<StartConversationResponse>();

                // The server's id wins over the one just minted: it is what every later call
                // and every SignalR group is keyed on.
                _chatStateService.ConversationId = result?.ConversationId ?? string.Empty;

                // The server echoes the id it was given, so the group joined above is the right one.
                // A different id would leave the pre-join on a group nobody publishes to, so the group is switched.
                if (!string.Equals(_chatStateService.ConversationId, request.ConversationId, StringComparison.Ordinal))
                {
                    _logger.LogWarning("Server returned conversation id {ServerId} instead of the requested {RequestedId}; rejoining",
                        _chatStateService.ConversationId, request.ConversationId);

                    await _signalR.LeaveConversation(request.ConversationId);
                    await _signalR.JoinConversation(_chatStateService.ConversationId);
                }

                // A returning visitor resumes this conversation.
                await _storage.SaveConversationIdAsync(_chatStateService.ConversationId);

                _logger.LogInformation("Conversation started: {ConversationId}", _chatStateService.ConversationId);
                return true;
            }

            // A refused start is shown to the visitor with its status.
            string errorContent = await response.Content.ReadAsStringAsync();
            _logger.LogError("Failed to start conversation: {StatusCode} - {Error}", response.StatusCode, errorContent);
            _chatStateService.AddErrorBanner($"Failed to start conversation: {response.StatusCode}", "conversation_start_http_error", 12);

            // The conversation never came into being, so the group joined above has nothing behind it.
            // Harmless in itself, but a retry mints a new id and would leave this one behind for the
            // life of the circuit.
            await _signalR.LeaveConversation(request.ConversationId);
            return false;
        }
        catch (Exception ex)
        {
            // Morgana unreachable: the visitor is told and the group joined for nothing is left.
            _logger.LogError(ex, "StartConversation exception");
            _chatStateService.AddErrorBanner($"Connection error: {ex.Message}", "conversation_start_exception", 20);

            await _signalR.LeaveConversation(request.ConversationId);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> ResumeConversationAsync(string savedConversationId)
    {
        // Scopes every log line below to this conversation id, so a single id lets an
        // operator pull the whole resume out of the log stream, failures included.
        using IDisposable? scope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["ConversationId"] = savedConversationId
        });

        try
        {
            _logger.LogInformation("Attempting to resume conversation {ConversationId}", savedConversationId);

            // Morgana reports what the page needs to redraw: the active agent and the dust level.
            HttpResponseMessage response = await _http.PostAsync(
                $"/api/morgana/conversation/{savedConversationId}/resume", null);

            // The conversation is known: its state and its transcript are restored.
            if (response.IsSuccessStatusCode)
            {
                ResumeConversationResponse? result = await response.Content
                    .ReadFromJsonAsync<ResumeConversationResponse>();

                // The page serves the resumed conversation from here.
                _chatStateService.ConversationId = result?.ConversationId ?? savedConversationId;

                // The gauge shows the dust left at once, not a full bar corrected only after the next turn.
                // Null leaves the gauge hidden, since Morgana limits no dust.
                _chatStateService.DustLevel = result?.ConversationMetadata.DustLevel;

                // A resumed page may send before any push reaches it, so the wait is taken from the resume answer.
                _chatStateService.ReplyTimeout = result?.ConversationMetadata.ReplyTimeoutSeconds is { } replyTimeoutSeconds
                    ? TimeSpan.FromSeconds(replyTimeoutSeconds)
                    : null;

                // A conversation already out of dust shows the lockout banner at once, with the same reason as the live one,
                // so the composer locks and the New Conversation button shows before a doomed message is sent.
                if (!string.IsNullOrEmpty(result?.DustExhaustedMessage))
                    _chatStateService.AddErrorBanner(result.DustExhaustedMessage, ChannelErrorReasons.DustBudgetExhausted);

                // Morgana holds the conversation when no agent is active.
                if (string.IsNullOrEmpty(result?.ActiveAgent)
                    || string.Equals(result.ActiveAgent, "Morgana", StringComparison.OrdinalIgnoreCase))
                {
                    _chatStateService.CurrentAgentName = "Morgana";
                }
                // An active agent is named beside Morgana, as its replies are signed.
                else
                {
                    _chatStateService.CurrentAgentName = $"Morgana ({char.ToUpperInvariant(result.ActiveAgent[0]) + result.ActiveAgent[1..]})";
                }

                // Pushes of this conversation reach the page from here.
                await _signalR.JoinConversation(_chatStateService.ConversationId);

                // The transcript as Morgana kept it.
                return await LoadHistoryAsync();
            }

            // 404 is the ordinary case of a stale browser: the saved id outlived the server's
            // knowledge of it. Anything else is a real failure, but both recover the same way.
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                _logger.LogWarning("Conversation {ConversationId} not found, starting fresh", savedConversationId);
            else
                _logger.LogError("Resume error {StatusCode}", response.StatusCode);

            // The visitor gets a fresh conversation either way.
            return await FallbackToNewConversationAsync();
        }
        catch (Exception ex)
        {
            // Morgana unreachable on resume: a fresh conversation is tried in its place.
            _logger.LogError(ex, "ResumeConversation exception");
            return await FallbackToNewConversationAsync();
        }
    }

    /// <inheritdoc />
    public async Task EndConversationAsync()
    {
        // Snapshot the id before anything else touches the state, so the whole teardown targets
        // one conversation even though the caller clears the state right after.
        string conversationId = _chatStateService.ConversationId;

        // Nothing to end when no conversation was ever established: a first load or a start that failed.
        if (string.IsNullOrEmpty(conversationId))
            return;

        // Scopes every log line below to this conversation id, so a single id lets an
        // operator pull the whole end out of the log stream, failures included.
        using IDisposable? scope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["ConversationId"] = conversationId
        });

        try
        {
            // Leave the SignalR group first, so anything still in flight for this conversation
            // does not land on a circuit that has already walked away from it.
            await _signalR.LeaveConversation(conversationId);

            // Morgana stops the conversation's actors: the manager, the supervisor, the guard, the classifier, the router and the agents.
            // The persisted history is untouched: this frees actors and deletes no data.
            await _http.PostAsync($"/api/morgana/conversation/{conversationId}/end", content: null);

            _logger.LogInformation("Conversation ended: {ConversationId}", conversationId);
        }
        catch (Exception ex)
        {
            // Best-effort cleanup: the user is leaving this conversation either way and the next
            // load starts a fresh one. A failure here costs idle actors on the server, never the
            // user's ability to move on, so it is logged rather than surfaced.
            _logger.LogWarning(ex, "EndConversation failed for {ConversationId}", conversationId);
        }
    }

    /// <inheritdoc />
    public async Task ClearConversationAsync()
    {
        _logger.LogInformation("Clearing conversation from storage");
        await _storage.ClearConversationIdAsync();
    }

    /// <inheritdoc />
    public async Task<string?> GetSavedConversationIdAsync()
    {
        // The id decrypted from the browser's storage.
        return await _storage.GetConversationIdAsync();
    }

    /// <inheritdoc />
    public async Task<bool> SendMessageAsync(string text)
    {
        // Scopes every log line below to this conversation id, so a single id lets an
        // operator pull the whole send out of the log stream, failures included.
        using IDisposable? scope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["ConversationId"] = _chatStateService.ConversationId
        });

        try
        {
            // The visitor's words, which Morgana answers over the hub.
            SendMessageRequest request = new SendMessageRequest(
                Text: text);
            HttpResponseMessage response = await _http.PostAsJsonAsync(
                $"/api/morgana/conversation/{_chatStateService.ConversationId}/message", request);

            _logger.LogInformation("Message sent, response status: {StatusCode}", response.StatusCode);

            // A refused message frees the composer again.
            if (!response.IsSuccessStatusCode)
            {
                // A limit was hit and Morgana has already told the user why over the channel: Cauldron adds nothing of its own.
                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    _logger.LogWarning("SendMessage refused on a limit: {StatusCode}", response.StatusCode);

                    // Morgana refused the turn on a limit and recorded nothing: the screen goes back to how it
                    // was before the send, with the user's words in the composer.
                    _chatStateService.WithdrawTurn();
                    return false;
                }

                // Any other refusal is shown in the transcript, the composer reopened for a retry.
                _logger.LogError("SendMessage failed: {StatusCode}", response.StatusCode);
                _chatStateService.RemoveTypingIndicator();
                _chatStateService.AddChatError($"Message not sent: {response.StatusCode}. Please try again.", "send_message_http_error");
                _chatStateService.IsSending = false;
                return false;
            }

            // Morgana took the message: from here only its reply can free the composer, so the
            // reply gets a deadline of silence.
            NoteReplyActivity();
            return true;
        }
        catch (Exception ex)
        {
            // Morgana unreachable: the error is shown in the transcript and the composer reopened.
            _logger.LogError(ex, "SendMessage exception");
            _chatStateService.RemoveTypingIndicator();
            _chatStateService.AddChatError(
                $"Connection error: {ex.Message}. Please try again.",
                "send_message_exception");
            _chatStateService.IsSending = false;
            return false;
        }
    }

    /// <inheritdoc />
    public void NoteReplyActivity()
    {
        lock (_replyDeadlineLock)
        {
            // Every sign of life restarts the wait from now.
            _replyDeadline?.Dispose();

            // Morgana states the wait on her first message and on resume, so a turn is never sent before it is known.
            if (_chatStateService.ReplyTimeout is not { } replyTimeout)
            {
                _replyDeadline = null;
                return;
            }

            // The page hears of the silence once, when the wait runs out.
            _replyDeadline = new Timer(_ => OnReplyDeadlineExpired?.Invoke(), null, replyTimeout, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Stops waiting on the turn in flight, leaving no deadline standing.</summary>
    private void CancelReplyDeadline()
    {
        lock (_replyDeadlineLock)
        {
            // The deadline goes with whatever it was waiting for.
            _replyDeadline?.Dispose();
            _replyDeadline = null;
        }
    }

    /// <inheritdoc />
    public void AbandonTurn()
    {
        // The turn is given up: no deadline, no half-typed reply, the composer reopened with the reason shown.
        CancelReplyDeadline();
        _streamingService.AbandonStreaming();
        _chatStateService.RemoveTypingIndicator();
        _chatStateService.IsSending = false;
        _chatStateService.AddChatError(
            $"No answer from Morgana after {_chatStateService.ReplyTimeout?.TotalSeconds:0} seconds. The turn may be lost: ask again.",
            "reply_timeout");
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ChannelMessage>> GetMissedRepliesAsync()
    {
        // Nothing is waiting, so nothing was missed while this client was away.
        if (!IsAwaitingReply || string.IsNullOrWhiteSpace(_chatStateService.ConversationId))
            return [];

        // An unreadable or empty record holds no reply to catch up.
        ConversationHistoryResponse? history = await _history.GetHistoryAsync(_chatStateService.ConversationId);
        if (history?.Messages is not { Length: > 0 })
            return [];

        // Morgana's side past the latest reply this client was given is what it pushed while the
        // client was away. The user's own turn is already on screen, so it is never taken back.
        MorganaChatMessage[] missed =
        [
            .. history.Messages.Where(message => message.Type == ChatMessageType.Assistant
                                                 && message.Timestamp > _chatStateService.LatestDeliveredTimestamp)
        ];

        if (missed.Length > 0)
            _logger.LogInformation("Recovered {Count} message(s) delivered while this client was away", missed.Length);

        // The latest reply carries the conversation's description as it stands now, as its own push would have.
        return
        [
            .. missed.Select((message, index) => ToChannelMessage(
                message, index == missed.Length - 1 ? history.ConversationMetadata : null))
        ];
    }

    /// <summary>Redraws the resumed conversation from Morgana's record and resumes the wait of a turn still in flight; true when it is on screen.</summary>
    private async Task<bool> LoadHistoryAsync()
    {
        try
        {
            // Morgana's record of the conversation.
            ConversationHistoryResponse? history = await _history.GetHistoryAsync(_chatStateService.ConversationId);

            // A conversation with nothing said has nothing to resume: a fresh one replaces it.
            if (history?.Messages is not { Length: > 0 })
            {
                _logger.LogWarning("No history found for conversation {ConversationId}", _chatStateService.ConversationId);
                return await FallbackToNewConversationAsync();
            }

            _logger.LogInformation("Retrieved {Count} messages from history", history.Messages.Length);

            // Everything Morgana said so far is on screen from here: a later catch-up starts past it.
            foreach (MorganaChatMessage message in history.Messages.Where(message => message.Type == ChatMessageType.Assistant))
                _chatStateService.NoteDelivered(message.Timestamp);

            // Redrawn exactly as Morgana kept it: the handover lines are hers and travel in the
            // history like anything else she said, so there is nothing left to infer here.
            foreach (MorganaChatMessage historyMessage in history.Messages)
                _chatStateService.ChatMessages.Add(MapToChatMessage(historyMessage));

            // Morgana saves a phrase the moment it arrives, so a history ending on the visitor's
            // own words means that turn is still being worked on — typically a reload during it.
            bool isTurnInFlight = history.Messages.Last().Type == ChatMessageType.User;

            // The composer stays shut and the wait resumes where the reload cut it. This also
            // recovers a reply pushed into the gap before this client rejoined its group: the
            // deadline asks the history first and gives up only on a turn with nothing there.
            if (isTurnInFlight)
            {
                _chatStateService.IsSending = true;
                _chatStateService.AddTypingIndicator();
                NoteReplyActivity();

                _logger.LogInformation("History ends on a user turn: resuming the wait for Morgana's reply");
            }

            return true;
        }
        catch (Exception ex)
        {
            // A record that cannot be redrawn is replaced by a fresh conversation.
            _logger.LogWarning(ex, "Failed to load history");
            return await FallbackToNewConversationAsync();
        }
    }

    /// <summary>
    /// Projects a history message from the wire contract onto the page's own message, which carries state the server never knows.
    /// The type is mapped exhaustively, so a value added on the server breaks here rather than landing on the wrong styling.
    /// </summary>
    private static ChatMessage MapToChatMessage(MorganaChatMessage message) =>
        // The message as the page draws it, with the buttons and the card it was delivered with.
        new()
        {
            ConversationId = message.ConversationId,
            Text = message.Text,
            Timestamp = message.Timestamp,
            // A type the page does not know breaks loudly.
            Type = message.Type switch
            {
                ChatMessageType.User => MessageType.User,
                ChatMessageType.Assistant => MessageType.Assistant,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(message), message.Type, "Unmapped history message type.")
            },
            AgentName = message.AgentName,
            QuickReplies = message.QuickReplies,
            RichCard = message.RichCard,
            IsLastHistoryMessage = message.IsLastHistoryMessage
        };

    /// <summary>
    /// Turns a reply read back from the history into the message a push would have carried.
    /// It goes on screen through the same path as a reply that was never missed.
    /// </summary>
    private static ChannelMessage ToChannelMessage(MorganaChatMessage message, ConversationMetadata? conversationMetadata) =>
        // The push the reply would have been.
        new()
        {
            ConversationMetadata = conversationMetadata,
            ConversationId = message.ConversationId,
            Text = message.Text,
            Timestamp = message.Timestamp,
            AgentName = message.AgentName,
            AgentCompleted = message.AgentCompleted,
            QuickReplies = message.QuickReplies,
            RichCard = message.RichCard
        };

    /// <summary>Replaces an unusable conversation with a fresh one; true when the fresh one started.</summary>
    private async Task<bool> FallbackToNewConversationAsync()
    {
        // Drop the unusable id first: leaving it would make the next page load retry the same
        // dead conversation and fall through to here again.
        await _storage.ClearConversationIdAsync();

        // The visitor carries on in a new conversation.
        return await StartConversationAsync();
    }

    /// <summary>Drops the deadline of a turn still in flight when the visitor's circuit closes.</summary>
    public void Dispose()
    {
        // A page closed mid-turn would otherwise keep a deadline counting down for a screen that is gone.
        CancelReplyDeadline();

        // Unsealed like every default service, so a derived service's finalizer would find nothing left to release.
        GC.SuppressFinalize(this);
    }
}