using Cauldron.Interfaces;
using Cauldron.Messages;
using Morgana.Contracts;

namespace Cauldron.Services;

/// <summary>
/// Drives the typewriter effect for streaming responses: buffers the chunks arriving from SignalR
/// and releases them into the visible message a few characters per tick.
/// </summary>
public class StreamingService : IStreamingService
{
    private readonly IChatStateService _chatStateService;
    private readonly IConfiguration _configuration;

    private string _streamingBuffer = string.Empty;
    private Timer? _typewriterTimer;
    private ChatMessage? _currentStreamingMessage;

    /// <summary>
    /// Raised when the UI should re-render (after each typewriter tick or streaming state change).
    /// </summary>
    public event Action? OnStateChanged;

    /// <summary>
    /// True if a streaming session is active.
    /// </summary>
    public bool IsStreaming => _currentStreamingMessage != null;

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
        // A null current message means this is the opening chunk of a new response, so the
        // session has to be built before the text can go anywhere.
        if (_currentStreamingMessage == null)
        {
            // The agent is no longer "thinking", it is answering: the placeholder makes way
            _chatStateService.RemoveTypingIndicator();

            // Starts empty and grows one tick at a time; the agent name is taken from current
            // state because the message carrying the authoritative one has not arrived yet.
            _currentStreamingMessage = new ChatMessage
            {
                ConversationId = _chatStateService.ConversationId,
                Text = string.Empty,
                Role = "assistant",
                Timestamp = DateTime.UtcNow,
                AgentName = _chatStateService.CurrentAgentName,
                IsStreaming = true
            };

            _chatStateService.ChatMessages.Add(_currentStreamingMessage);

            // Typewriter pace, re-read per session so a config change needs no restart.
            // Both fall back to their defaults on a missing, unparsable or non-positive value.
            int.TryParse(_configuration["Cauldron:StreamingResponse:TypewriterTickMilliseconds"], out int tickMs);
            if (tickMs <= 0)
                tickMs = 15;
            int.TryParse(_configuration["Cauldron:StreamingResponse:TypewriterTickChars"], out int tickChars);
            if (tickChars <= 0)
                tickChars = 1;

            // Defensive: a timer left over from a session that never reached StopStreaming
            if (_typewriterTimer is not null)
                await _typewriterTimer.DisposeAsync();

            // Chars-per-tick travels as the timer state, so the callback stays stateless
            _typewriterTimer = new Timer(TypewriterTick, tickChars, 0, tickMs);

            OnStateChanged?.Invoke();
        }

        // Chunks are queued, never rendered directly: the timer decides the pace at which
        // they surface, which is what makes the text type out instead of appearing in bursts.
        _streamingBuffer += chunkText;
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
        // Nothing was streaming: this response arrived complete, and the caller handles it
        if (_currentStreamingMessage == null)
            return;

        // Overwrite rather than append, and drop whatever was still queued: the buffered tail
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

    /// <summary>
    /// Timer callback: consumes characters from the buffer at typewriter speed.
    /// Auto-stops when the buffer is empty and streaming is complete.
    /// </summary>
    private void TypewriterTick(object? state)
    {
        // The session was already torn down; a tick may still be in flight
        if (_currentStreamingMessage == null)
            return;

        if (string.IsNullOrEmpty(_streamingBuffer))
        {
            // Buffer drained and the server has spoken: the session is genuinely over. If the
            // flag is still set the buffer is merely outrunning the network, so keep ticking.
            if (!_currentStreamingMessage.IsStreaming)
            {
                StopStreaming();
            }
            return;
        }

        // Clamped to what is actually buffered, so a fast tick rate cannot overrun the text
        int charsToTake = Math.Min((int)state!, _streamingBuffer.Length);
        string nextChars = _streamingBuffer[..charsToTake];
        _streamingBuffer = _streamingBuffer[charsToTake..];

        _currentStreamingMessage.Text += nextChars;

        OnStateChanged?.Invoke();
    }

    private void StopStreaming()
    {
        _typewriterTimer?.Dispose();
        _typewriterTimer = null;
        _streamingBuffer = string.Empty;

        // Nulling this is what flips IsStreaming false and frees the service for the next response
        _currentStreamingMessage = null;

        // Final re-render, so the message settles without the streaming styling
        OnStateChanged?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        if (_typewriterTimer is not null)
            await _typewriterTimer.DisposeAsync();
    }
}
