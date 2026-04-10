using Cauldron.Messages;

namespace Cauldron.Interfaces;

/// <summary>
/// Service for managing streaming state: chunk buffering, typewriter timer, and streaming lifecycle.
/// </summary>
public interface IStreamingService : IAsyncDisposable
{
    /// <summary>
    /// Raised when the UI should re-render (after each typewriter tick or streaming state change).
    /// </summary>
    event Action? OnStateChanged;

    /// <summary>
    /// True if a streaming session is active.
    /// </summary>
    bool IsStreaming { get; }

    /// <summary>
    /// Handles an incoming streaming chunk from SignalR.
    /// On the first chunk, creates the streaming message and starts the typewriter timer.
    /// </summary>
    Task HandleChunkAsync(string chunkText);

    /// <summary>
    /// Finalizes the current streaming session with the complete message metadata.
    /// The typewriter timer continues draining the buffer naturally before cleanup.
    /// </summary>
    void FinalizeStreaming(ChannelMessage completeMessage);
}