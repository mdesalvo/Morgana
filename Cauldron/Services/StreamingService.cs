using Cauldron.Interfaces;
using Cauldron.Messages;

namespace Cauldron.Services;

/// <summary>
/// Manages streaming state: chunk buffering, typewriter timer, and streaming lifecycle.
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
        // First chunk - initialize streaming
        if (_currentStreamingMessage == null)
        {
            _chatStateService.RemoveTypingIndicator();

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

            int.TryParse(_configuration["Cauldron:StreamingResponse:TypewriterTickMilliseconds"], out int tickMs);
            if (tickMs <= 0)
                tickMs = 15;
            int.TryParse(_configuration["Cauldron:StreamingResponse:TypewriterTickChars"], out int tickChars);
            if (tickChars <= 0)
                tickChars = 1;

            if (_typewriterTimer is not null)
                await _typewriterTimer.DisposeAsync();

            _typewriterTimer = new Timer(TypewriterTick, tickChars, 0, tickMs);

            OnStateChanged?.Invoke();
        }

        _streamingBuffer += chunkText;
    }

    /// <summary>
    /// Finalizes the current streaming session with the complete message metadata.
    /// The typewriter timer continues draining the buffer naturally before cleanup.
    /// </summary>
    public void FinalizeStreaming(SignalRMessage completeMessage)
    {
        if (_currentStreamingMessage == null)
            return;

        _currentStreamingMessage.QuickReplies = completeMessage.QuickReplies;
        _currentStreamingMessage.RichCard = completeMessage.RichCard;
        _currentStreamingMessage.AgentName = completeMessage.AgentName;
        _currentStreamingMessage.IsStreaming = false;

        // Timer continues draining buffer - will auto-stop when empty
    }

    /// <summary>
    /// Timer callback: consumes characters from the buffer at typewriter speed.
    /// Auto-stops when the buffer is empty and streaming is complete.
    /// </summary>
    private void TypewriterTick(object? state)
    {
        if (_currentStreamingMessage == null)
            return;

        if (string.IsNullOrEmpty(_streamingBuffer))
        {
            if (!_currentStreamingMessage.IsStreaming)
            {
                StopStreaming();
            }
            return;
        }

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
        _currentStreamingMessage = null;

        OnStateChanged?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        if (_typewriterTimer is not null)
            await _typewriterTimer.DisposeAsync();
    }
}
