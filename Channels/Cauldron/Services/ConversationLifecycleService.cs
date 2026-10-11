using Cauldron.Interfaces;
using Cauldron.Messages;
using Morgana.Contracts;

namespace Cauldron.Services;

/// <summary>
/// Manages conversation lifecycle: start, resume, clear.
/// Coordinates between HTTP API, SignalR groups and local storage.
/// </summary>
public class ConversationLifecycleService : IConversationLifecycleService, IDisposable
{
    /// <summary>Calls Morgana's conversation REST API with the channel's token.</summary>
    private readonly HttpClient _http;

    /// <summary>Joins and leaves the SignalR groups that deliver the replies.</summary>
    private readonly SignalRService _signalR;

    /// <summary>Keeps the conversation id across browser sessions.</summary>
    private readonly IConversationStorageService _storage;

    /// <summary>Reads back what Morgana kept of the conversation.</summary>
    private readonly IConversationHistoryService _history;

    /// <summary>Holds what is on screen for this circuit.</summary>
    private readonly IChatStateService _chatStateService;

    /// <summary>Owns the response that is being typed out.</summary>
    private readonly IStreamingService _streamingService;

    /// <summary>Receives the lifecycle's log lines.</summary>
    private readonly ILogger _logger;

    /// <summary>Fallback for <c>Cauldron:ReplyTimeoutSeconds</c> when absent or non-positive.</summary>
    private const int DefaultReplyTimeoutSeconds = 120;

    /// <summary>
    /// How long a turn may go completely silent before it is given up on. It measures silence, not
    /// the length of the turn: every chunk pushes the deadline back, so a long answer still arriving
    /// is never given up on. From <c>Cauldron:ReplyTimeoutSeconds</c>.
    /// </summary>
    private readonly TimeSpan _replyTimeout;

    /// <summary>Counts down the silence on the turn in flight; null when nothing is being waited for.</summary>
    private Timer? _replyDeadline;

    /// <summary>Guards the deadline against a reply and a shutdown reaching it at the same moment.</summary>
    private readonly Lock _replyDeadlineLock = new();

    /// <inheritdoc />
    public event Action? OnReplyDeadlineExpired;

    /// <inheritdoc />
    public bool IsAwaitingReply => _chatStateService.IsSending || _chatStateService.HasTypingIndicator();

    /// <summary>
    /// Creates the lifecycle service of one chat session and reads the reply deadline from configuration.
    /// </summary>
    public ConversationLifecycleService(
        HttpClient http,
        SignalRService signalR,
        IConversationStorageService storage,
        IConversationHistoryService history,
        IChatStateService chatState,
        IStreamingService streaming,
        IConfiguration configuration,
        ILogger logger)
    {
        _http = http;
        _signalR = signalR;
        _storage = storage;
        _history = history;
        _chatStateService = chatState;
        _streamingService = streaming;
        _logger = logger;

        // A non-positive wait would give up on every turn the instant it is sent, so it falls back too
        int replyTimeoutSeconds = configuration.GetValue<int?>("Cauldron:ReplyTimeoutSeconds") ?? DefaultReplyTimeoutSeconds;
        _replyTimeout = TimeSpan.FromSeconds(replyTimeoutSeconds > 0 ? replyTimeoutSeconds : DefaultReplyTimeoutSeconds);
    }

    /// <summary>
    /// Starts a new conversation with Morgana backend. Returns true when it started.
    /// </summary>
    public async Task<bool> StartConversationAsync()
    {
        // Cauldron is the reference channel: announces itself by name and declares
        // full capabilities at handshake, so Morgana persists the metadata and stops
        // relying on hard-coded defaults.
        StartConversationRequest request = new(
            ConversationId: Guid.NewGuid().ToString("N"),
            ChannelMetadata: CauldronChannelMetadata.Profile);

        using IDisposable? scope = BeginConversationScope(request.ConversationId);

        try
        {
            _logger.LogInformation("Starting new conversation...");

            // Joined before the conversation exists, not after it starts. The presentation is pushed
            // the instant Morgana has a conversation and a SignalR message sent to a group with no
            // members is discarded without a trace — so a join issued after the start response is a
            // race, lost every time the greeting skips the LLM (no agents configured, presenter
            // failure) and therefore costs nothing to produce. Joining first removes the race instead
            // of narrowing it: the id is minted here and a group is joinable before anything is
            // keyed on it. On the server side this is the documented ordering, see
            // MorganaHub.JoinConversation.
            await _signalR.JoinConversation(request.ConversationId);

            // Asks Morgana to create the conversation under the id just minted
            HttpResponseMessage response = await _http.PostAsJsonAsync(
                "/api/morgana/conversation/start", request);

            // Morgana accepted the start: the ids are reconciled and the conversation is remembered
            if (response.IsSuccessStatusCode)
            {
                StartConversationResponse? startResponse = await response.Content
                    .ReadFromJsonAsync<StartConversationResponse>();

                // The server's id wins over the one just minted: it is what every later call
                // and every SignalR group is keyed on.
                _chatStateService.ConversationId = startResponse?.ConversationId ?? string.Empty;

                // Today the server echoes the id it was given, so the group joined above is already
                // the right one. Should it ever hand back a different one, the pre-join landed on a
                // group nobody publishes to: correct it here rather than silently receive nothing.
                if (!string.Equals(_chatStateService.ConversationId, request.ConversationId, StringComparison.Ordinal))
                {
                    _logger.LogWarning("Server returned conversation id {ServerId} instead of the requested {RequestedId}; rejoining",
                        _chatStateService.ConversationId, request.ConversationId);

                    // The pre-join landed on a group nobody publishes to
                    await _signalR.LeaveConversation(request.ConversationId);
                    await _signalR.JoinConversation(_chatStateService.ConversationId);
                }

                // A reload finds this id and resumes the conversation instead of starting another
                await _storage.SaveConversationIdAsync(_chatStateService.ConversationId);

                _logger.LogInformation("Conversation started: {ConversationId}", _chatStateService.ConversationId);
                return true;
            }

            // Morgana refused the start: the visitor is told and the attempt ends here
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
            // Morgana unreachable or the response unreadable: reported as a banner, the page stays usable
            _logger.LogError(ex, "StartConversation exception");
            _chatStateService.AddErrorBanner($"Connection error: {ex.Message}", "conversation_start_exception", 20);

            // Same as the refusal: the group joined above has nothing behind it
            await _signalR.LeaveConversation(request.ConversationId);
            return false;
        }
    }

    /// <summary>
    /// Resumes an existing conversation from storage.
    /// Falls back to StartConversationAsync on any failure. Returns true when the conversation was
    /// resumed or a new one started.
    /// </summary>
    public async Task<bool> ResumeConversationAsync(string savedConversationId)
    {
        using IDisposable? scope = BeginConversationScope(savedConversationId);

        try
        {
            _logger.LogInformation("Attempting to resume conversation {ConversationId}", savedConversationId);

            // Asks Morgana to bring the saved conversation back from its record
            HttpResponseMessage response = await _http.PostAsync(
                $"/api/morgana/conversation/{savedConversationId}/resume", null);

            // Morgana still knows the conversation: its state is restored on screen
            if (response.IsSuccessStatusCode)
            {
                ResumeConversationResponse? resumeResponse = await response.Content
                    .ReadFromJsonAsync<ResumeConversationResponse>();

                // The saved id is the fallback when the response carries none
                _chatStateService.ConversationId = resumeResponse?.ConversationId ?? savedConversationId;

                // Rehydrate the dust gauge from the resumed conversation's budget so the
                // widget reflects real residual dust immediately, not a pristine bar that
                // only corrects itself after the first post-resume turn. Null leaves the
                // indicator hidden (dust limiting disabled on Morgana).
                _chatStateService.DustLevel = resumeResponse?.DustLevel;

                // The resumed conversation is already dust-dead: re-surface the canonical
                // terminal banner up front (same ErrorReason as the live lockout, so the
                // input lock, the guaranteed New Conversation button and the non-fading
                // purple styling all engage) instead of letting the user discover the
                // wall by firing a message that is instantly rejected.
                if (!string.IsNullOrEmpty(resumeResponse?.DustExhaustedMessage))
                    _chatStateService.AddErrorBanner(resumeResponse.DustExhaustedMessage, ChannelErrorReasons.DustBudgetExhausted);

                // The header shows the specialist that was engaged when the visitor left
                _chatStateService.ResumeAgent(resumeResponse?.ActiveAgent);

                // Replies are pushed to the group: joined before the history is read so none falls between
                await _signalR.JoinConversation(_chatStateService.ConversationId);

                // The transcript is redrawn from Morgana's record
                return await LoadHistoryAsync();
            }

            // 404 is the ordinary case of a stale browser: the saved id outlived the server's
            // knowledge of it. Anything else is a real failure, but both recover the same way.
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                _logger.LogWarning("Conversation {ConversationId} not found, starting fresh", savedConversationId);
            else
                _logger.LogError("Resume error {StatusCode}", response.StatusCode);

            // Both outcomes leave the visitor with a fresh conversation
            return await FallbackToNewConversationAsync();
        }
        catch (Exception ex)
        {
            // Any failure while resuming leaves the visitor with a fresh conversation rather than a dead page
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

        using IDisposable? scope = BeginConversationScope(conversationId);

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
            // Best-effort cleanup: the user is leaving this conversation either way and the next
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

        // Without a saved id the next load starts a fresh conversation instead of resuming this one
        await _storage.ClearConversationIdAsync();
    }

    /// <summary>
    /// Checks storage for an existing conversation ID.
    /// </summary>
    public async Task<string?> GetSavedConversationIdAsync()
    {
        // The id of the conversation this browser left behind; null for a first-time visitor
        return await _storage.GetConversationIdAsync();
    }

    /// <summary>
    /// Sends a user message to the Morgana backend. Returns true when Morgana took it.
    /// </summary>
    public async Task<bool> SendMessageAsync(string text)
    {
        using IDisposable? scope = BeginConversationScope(_chatStateService.ConversationId);

        try
        {
            // The wire shape of a visitor's turn
            SendMessageRequest request = new SendMessageRequest(
                Text: text);

            // The reply does not come back here: it is pushed over SignalR
            HttpResponseMessage response = await _http.PostAsJsonAsync(
                $"/api/morgana/conversation/{_chatStateService.ConversationId}/message", request);

            _logger.LogInformation("Message sent, response status: {StatusCode}", response.StatusCode);

            // Morgana refused the turn: no reply will come, so the composer is freed with the error
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("SendMessage failed: {StatusCode}", response.StatusCode);
                FailTurn($"Message not sent: {response.StatusCode}. Please try again.", "send_message_http_error");
                return false;
            }

            // Morgana took the message: from here only its reply can free the composer, so give
            // that reply a deadline of silence
            NoteReplyActivity();
            return true;
        }
        catch (Exception ex)
        {
            // The turn may never have reached Morgana: same outcome as a refusal
            _logger.LogError(ex, "SendMessage exception");
            FailTurn($"Connection error: {ex.Message}. Please try again.", "send_message_exception");
            return false;
        }
    }

    /// <inheritdoc />
    public void NoteReplyActivity()
    {
        lock (_replyDeadlineLock)
        {
            // Restarts the silence count: the previous deadline no longer applies
            _replyDeadline?.Dispose();
            _replyDeadline = new Timer(_ => OnReplyDeadlineExpired?.Invoke(), null, _replyTimeout, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Stops waiting on the turn in flight, leaving no deadline standing.</summary>
    private void CancelReplyDeadline()
    {
        lock (_replyDeadlineLock)
        {
            // Nothing is waited for any more: the timer must not fire on a settled turn
            _replyDeadline?.Dispose();
            _replyDeadline = null;
        }
    }

    /// <inheritdoc />
    public void AbandonTurn()
    {
        // The deadline has fired or is moot: it must not fire again for this turn
        CancelReplyDeadline();

        // What a stream had revealed stays as the partial reply it is
        _streamingService.AbandonStreaming();

        // The notice tells the visitor that the answer may never come
        FailTurn(
            $"No answer from Morgana after {_replyTimeout.TotalSeconds:0} seconds. The turn may be lost — ask again.",
            "reply_timeout");
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ChannelMessage>> GetMissedRepliesAsync()
    {
        // Nothing is waiting, so nothing was missed while this client was away
        if (!IsAwaitingReply || string.IsNullOrWhiteSpace(_chatStateService.ConversationId))
            return [];

        // Morgana's record of the conversation is the only place a reply that was never pushed can be found
        ConversationHistoryResponse? history = await _history.GetHistoryAsync(_chatStateService.ConversationId);

        // An unreadable or empty record holds nothing to recover
        if (history?.Messages is not { Length: > 0 })
            return [];

        // Morgana's side past the latest reply this client was given is what it pushed while the
        // client was away. The user's own turn is already on screen, so it is never taken back.
        MorganaChatMessage[] missedReplies =
        [
            .. history.Messages.Where(message => message.Type == ChatMessageType.Assistant
                                                 && message.Timestamp > _chatStateService.LatestDeliveredTimestamp)
        ];

        if (missedReplies.Length > 0)
            _logger.LogInformation("Recovered {Count} message(s) delivered while this client was away", missedReplies.Length);

        // The latest reply carries the dust gauge as it stands now, as its own push would have
        return
        [
            .. missedReplies.Select((message, index) => ToChannelMessage(
                message, index == missedReplies.Length - 1 ? new ConversationMetadata(history.DustLevel) : null))
        ];
    }

    // =========================================================================
    // PRIVATE HELPERS
    // =========================================================================

    /// <summary>
    /// Scopes every log line that follows to one conversation, so its id pulls the whole
    /// attempt out of the log stream with the failures included.
    /// </summary>
    private IDisposable? BeginConversationScope(string conversationId) =>
        _logger.BeginScope(new Dictionary<string, object> { ["ConversationId"] = conversationId });

    /// <summary>
    /// Ends a turn that will get no reply: the composer is freed and the transcript records the failure.
    /// </summary>
    private void FailTurn(string text, string errorReason)
    {
        // The placeholder would otherwise keep pulsing for an answer that is not coming
        _chatStateService.RemoveTypingIndicator();

        // Banner for the detail and a vague line in the transcript for where the conversation broke
        _chatStateService.AddChatError(text, errorReason);

        // Lets the visitor type again
        _chatStateService.IsSending = false;
    }

    /// <summary>
    /// Redraws the transcript from Morgana's record and resumes the wait when the last turn is unanswered.
    /// </summary>
    private async Task<bool> LoadHistoryAsync()
    {
        try
        {
            // Morgana's record of what was said, in order
            ConversationHistoryResponse? history = await _history.GetHistoryAsync(_chatStateService.ConversationId);

            // A conversation without history cannot be shown: a fresh one replaces it
            if (history?.Messages is not { Length: > 0 })
            {
                _logger.LogWarning("No history found for conversation {ConversationId}", _chatStateService.ConversationId);
                return await FallbackToNewConversationAsync();
            }

            _logger.LogInformation("Retrieved {Count} messages from history", history.Messages.Length);

            // Everything Morgana said so far is on screen from here: a later catch-up starts past it
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
                // Composer shut and indicator back on, as before the reload
                _chatStateService.IsSending = true;
                _chatStateService.AddTypingIndicator();

                // Silence is measured from now: the reload erased the previous count
                NoteReplyActivity();

                _logger.LogInformation("History ends on a user turn: resuming the wait for Morgana's reply");
            }

            // The conversation is back on screen
            return true;
        }
        catch (Exception ex)
        {
            // An unreadable record is as unusable as a missing one
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

    /// <summary>
    /// Turns a reply read back from the history into the message a push would have carried, so it
    /// goes on screen through the same path as a reply that was never missed.
    /// </summary>
    private static ChannelMessage ToChannelMessage(MorganaChatMessage message, ConversationMetadata? conversationMetadata) =>
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

    /// <summary>
    /// Replaces a conversation that cannot be resumed with a brand-new one.
    /// </summary>
    private async Task<bool> FallbackToNewConversationAsync()
    {
        // Drop the unusable id first: leaving it would make the next page load retry the same
        // dead conversation and fall through to here again.
        await _storage.ClearConversationIdAsync();

        // The visitor lands in a fresh conversation rather than on a dead page
        return await StartConversationAsync();
    }

    /// <summary>Drops the deadline of a turn still in flight when the visitor's circuit closes.</summary>
    public void Dispose()
    {
        // A page closed mid-turn would otherwise keep a deadline counting down for a screen that is gone
        CancelReplyDeadline();

        // Unsealed like every default service, so a derived service's finalizer would find nothing left to release
        GC.SuppressFinalize(this);
    }
}