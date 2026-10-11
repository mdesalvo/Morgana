using System.Globalization;
using Cauldron.Interfaces;
using Cauldron.Messages;
using Morgana.Contracts;

namespace Cauldron.Services;

/// <summary>
/// Drives the typewriter effect for streaming responses: buffers the chunks arriving from SignalR
/// and releases them into the visible message a few characters per tick.
/// </summary>
/// <remarks>
/// Chunks and finalization arrive on the circuit's thread while the typewriter ticks on the thread
/// pool, so every read and write of the session goes through <see cref="_sessionLock"/>.
/// </remarks>
public class StreamingService : IStreamingService
{
    /// <summary>Holds the message list that the streaming message is added to.</summary>
    private readonly IChatStateService _chatStateService;

    /// <summary>Supplies the typewriter pace.</summary>
    private readonly IConfiguration _configuration;

    /// <summary>
    /// Guards the buffer, the timer and the streaming message. Without it a finalization landing
    /// mid-tick empties the buffer under the tick's feet and the resulting exception on the timer
    /// thread brings the whole process down.
    /// </summary>
    private readonly Lock _sessionLock = new();

    /// <summary>Holds the received text that the typewriter has not revealed yet.</summary>
    private string _streamingBuffer = string.Empty;

    /// <summary>Releases the buffer into the message; null when no response is being typed out.</summary>
    private Timer? _typewriterTimer;

    /// <summary>The message on screen that the typewriter is writing into; null between responses.</summary>
    private ChatMessage? _currentStreamingMessage;

    /// <summary>
    /// Raised when the UI should re-render (after each typewriter tick or streaming state change).
    /// </summary>
    public event Action? OnStateChanged;

    /// <summary>
    /// True while a response is still arriving. A finalized response whose last tick has not yet
    /// torn the session down no longer counts: the next message must not be taken as its ending.
    /// </summary>
    public bool IsStreaming
    {
        get
        {
            lock (_sessionLock)
                return _currentStreamingMessage is { IsStreaming: true };
        }
    }

    /// <summary>
    /// True while text already received is still being revealed. A turn in this state is alive:
    /// Morgana has spoken and the screen is simply catching up with it.
    /// </summary>
    public bool IsRevealing
    {
        get
        {
            lock (_sessionLock)
                return _streamingBuffer.Length > 0;
        }
    }

    /// <summary>
    /// Creates the streaming service of one chat session.
    /// </summary>
    public StreamingService(IChatStateService chatState, IConfiguration configuration)
    {
        _chatStateService = chatState;
        _configuration = configuration;
    }

    /// <summary>
    /// Handles an incoming streaming chunk from SignalR.
    /// On the first chunk, creates the streaming message and starts the typewriter timer.
    /// </summary>
    public async Task HandleChunkAsync(string chunkText)
    {
        // Set only when a session begins here: the previous timer is disposed outside the lock
        Timer? leftoverTimer = null;
        bool isNewSession = false;

        lock (_sessionLock)
        {
            // No response in progress means this is the opening chunk of a new one. A finalized
            // session still waiting for its teardown tick counts as over, so its message is not
            // extended with the next response's text.
            if (_currentStreamingMessage is not { IsStreaming: true })
            {
                // The agent is no longer "thinking", it is answering: the placeholder makes way
                _chatStateService.RemoveTypingIndicator();

                // Starts empty and grows one tick at a time; the agent name is taken from current
                // state because the message carrying the authoritative one has not arrived yet.
                _currentStreamingMessage = new ChatMessage
                {
                    ConversationId = _chatStateService.ConversationId,
                    Text = string.Empty,
                    Type = MessageType.Assistant,
                    Timestamp = DateTime.UtcNow,
                    AgentName = _chatStateService.CurrentAgentName,
                    IsStreaming = true
                };

                // On screen from the first chunk, empty, so the message appears where the typing indicator was
                _chatStateService.ChatMessages.Add(_currentStreamingMessage);

                // A previous session's leftovers must not leak into this response
                _streamingBuffer = string.Empty;

                // Typewriter pace, re-read per session so a config change needs no restart.
                // Both fall back to their defaults on a missing, unparsable or non-positive value.
                int.TryParse(_configuration["Cauldron:StreamingResponse:TypewriterTickMilliseconds"], NumberStyles.Integer, CultureInfo.InvariantCulture, out int tickMilliseconds);
                if (tickMilliseconds <= 0)
                    tickMilliseconds = 15;
                int.TryParse(_configuration["Cauldron:StreamingResponse:TypewriterTickChars"], NumberStyles.Integer, CultureInfo.InvariantCulture, out int tickChars);
                if (tickChars <= 0)
                    tickChars = 1;

                // The previous session's timer, when its teardown tick has not run yet, is stopped
                // once the lock is released
                leftoverTimer = _typewriterTimer;

                // Chars-per-tick travels as the timer state, so the callback stays stateless
                _typewriterTimer = new Timer(TypewriterTick, tickChars, 0, tickMilliseconds);
                isNewSession = true;
            }

            // Chunks are queued, never rendered directly: the timer decides the pace at which
            // they surface, which is what makes the text type out instead of appearing in bursts.
            _streamingBuffer += chunkText;
        }

        // Disposed outside the lock: waiting for an in-flight tick that needs the lock would deadlock
        if (leftoverTimer is not null)
            await leftoverTimer.DisposeAsync();

        // The streaming message just appeared: the page repaints to show it
        if (isNewSession)
            OnStateChanged?.Invoke();
    }

    /// <summary>
    /// Finalizes the current streaming session with the complete message metadata.
    /// </summary>
    /// <remarks>
    /// The server is the source of truth for the final text: Morgana's channel adapter may rewrite
    /// the message before delivery, in which case the streamed chunks were a preview of something
    /// that no longer applies and must be replaced wholesale.
    /// </remarks>
    public void FinalizeStreaming(ChannelMessage completeMessage)
    {
        lock (_sessionLock)
        {
            // Nothing was streaming: this response arrived complete and the caller handles it
            if (_currentStreamingMessage is not { IsStreaming: true })
                return;

            // Overwrite rather than append and drop whatever was still queued: the buffered tail
            // belongs to the pre-adaptation text and would duplicate what is now on screen.
            _currentStreamingMessage.Text = completeMessage.Text;
            _streamingBuffer = string.Empty;

            // Attachments only exist on the finished message, never on the chunks
            _currentStreamingMessage.QuickReplies = completeMessage.QuickReplies;
            _currentStreamingMessage.RichCard = completeMessage.RichCard;
            _currentStreamingMessage.AgentName = completeMessage.AgentName;

            // Clearing the flag is what lets the next tick tear the session down
            _currentStreamingMessage.IsStreaming = false;
        }
    }

    /// <summary>
    /// Closes a response Morgana stopped sending. What was revealed stays in the conversation as the
    /// partial reply it is, because it is what Morgana actually said; the session is freed so the
    /// next turn does not write into a message belonging to the abandoned one.
    /// </summary>
    public void AbandonStreaming()
    {
        lock (_sessionLock)
        {
            // No response was being streamed: nothing to close
            if (_currentStreamingMessage is null)
                return;

            // The message stays on screen as it is, no longer growing
            _currentStreamingMessage.IsStreaming = false;
            StopStreaming();
        }
    }

    /// <summary>
    /// Timer callback: consumes characters from the buffer at typewriter speed.
    /// Auto-stops when the buffer is empty and streaming is complete.
    /// </summary>
    private void TypewriterTick(object? state)
    {
        lock (_sessionLock)
        {
            // The session was already torn down; a tick may still be in flight
            if (_currentStreamingMessage == null)
                return;

            // Nothing left to reveal: the session either ends here or waits for more text
            if (string.IsNullOrEmpty(_streamingBuffer))
            {
                // Buffer drained and the server has spoken: the session is genuinely over. If the
                // flag is still set the buffer is merely outrunning the network, so keep ticking.
                if (_currentStreamingMessage.IsStreaming)
                    return;

                StopStreaming();
            }
            else
            {
                // Clamped to what is actually buffered, so a fast tick rate cannot overrun the text
                int charsToTake = Math.Min((int)state!, _streamingBuffer.Length);
                _currentStreamingMessage.Text += _streamingBuffer[..charsToTake];
                _streamingBuffer = _streamingBuffer[charsToTake..];
            }
        }

        // Raised outside the lock: the repaint may run right here on the timer thread and incoming
        // chunks must not wait for it to finish
        OnStateChanged?.Invoke();
    }

    /// <summary>
    /// Tears the session down. Must be called holding <see cref="_sessionLock"/>.
    /// </summary>
    private void StopStreaming()
    {
        // No more ticks: the response is over or abandoned
        _typewriterTimer?.Dispose();
        _typewriterTimer = null;

        // Text still queued belongs to a response that is no longer being typed
        _streamingBuffer = string.Empty;

        // Frees the service for the next response
        _currentStreamingMessage = null;
    }

    /// <summary>Stops the typewriter when the visitor's circuit closes.</summary>
    public async ValueTask DisposeAsync()
    {
        // Taken under the lock so a tick cannot be rearmed while the timer is being released
        Timer? timer;
        lock (_sessionLock)
        {
            timer = _typewriterTimer;

            // A tick already queued finds no session and returns
            _typewriterTimer = null;
        }

        // Waited out so no tick runs against a service that is already gone
        if (timer is not null)
            await timer.DisposeAsync();
    }
}
