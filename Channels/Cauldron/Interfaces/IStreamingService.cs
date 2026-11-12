using Morgana.Contracts;

namespace Cauldron.Interfaces;

/// <summary>Service for managing streaming state: chunk buffering, typewriter timer and streaming lifecycle.</summary>
public interface IStreamingService : IAsyncDisposable
{
    /// <summary>Raised when the UI should re-render (after each typewriter tick or streaming state change).</summary>
    event Action? OnStateChanged;

    /// <summary>True while a response is still arriving, so the next complete message is its ending.</summary>
    bool IsStreaming { get; }

    /// <summary>Handles an incoming streaming chunk from SignalR.</summary>
    // On the first chunk, creates the streaming message and starts the typewriter timer.
    Task HandleChunkAsync(string chunkText);

    /// <summary>Finalizes the current streaming session with the complete message metadata.</summary>
    // The server's text replaces the streamed one and whatever was still buffered is dropped.
    void FinalizeStreaming(ChannelMessage completeMessage);

    /// <summary>True while text already received is still being revealed.</summary>
    // A turn in this state is alive: Morgana has spoken and the screen is simply catching up with it.
    bool IsRevealing { get; }

    /// <summary>Closes a response Morgana stopped sending, leaving what was revealed on screen as the partial reply it is.</summary>
    // Does nothing when no response is being streamed.
    void AbandonStreaming();
}