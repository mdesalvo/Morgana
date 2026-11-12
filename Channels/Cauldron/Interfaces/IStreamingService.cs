using Morgana.Contracts;

namespace Cauldron.Interfaces;

/// <summary>Types out a streamed reply on screen: it buffers the chunks and reveals them a few characters at a time.</summary>
public interface IStreamingService : IAsyncDisposable
{
    /// <summary>Raised when the UI should re-render (after each typewriter tick or streaming state change).</summary>
    event Action? OnStateChanged;

    /// <summary>True while a response is still arriving, so the next complete message is its ending.</summary>
    bool IsStreaming { get; }

    /// <summary>
    /// Handles an incoming streaming chunk from SignalR.
    /// On the first chunk, creates the streaming message and starts the typewriter timer.
    /// </summary>
    Task HandleChunkAsync(string chunkText);

    /// <summary>
    /// Finalizes the current streaming session with the complete message metadata.
    /// The server's text replaces the streamed one and whatever was still buffered is dropped.
    /// </summary>
    void FinalizeStreaming(ChannelMessage completeMessage);

    /// <summary>
    /// True while text already received is still being revealed.
    /// A turn in this state is alive: Morgana has spoken and the screen is simply catching up with it.
    /// </summary>
    bool IsRevealing { get; }

    /// <summary>
    /// Closes a response Morgana stopped sending, leaving what was revealed on screen as the partial reply it is.
    /// Does nothing when no response is being streamed.
    /// </summary>
    void AbandonStreaming();
}