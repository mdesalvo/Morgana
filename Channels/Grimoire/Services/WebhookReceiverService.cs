using Morgana.Contracts;

namespace Grimoire.Services;

/// <summary>
/// Thin dispatcher invoked by the minimal-API <c>POST /morgana-hook</c> endpoint.
/// Decouples the HTTP surface from the console UI: <see cref="OnMessage"/> is wired
/// in <c>Program.cs</c> after both the receiver and the UI have been resolved from DI,
/// avoiding a constructor-level circular dependency.
/// </summary>
public sealed class WebhookReceiverService
{
    /// <summary>Callback invoked for every inbound message; wired in <c>Program.cs</c> to <see cref="ConsoleUiService.EnqueueIncoming"/>.</summary>
    public Action<ChannelMessage>? OnMessage { get; set; }

    /// <summary>Callback invoked for every inbound stream chunk; wired in <c>Program.cs</c> to <see cref="ConsoleUiService.EnqueueChunk"/>.</summary>
    public Action<StreamChunkRequest>? OnChunk { get; set; }

    /// <summary>Forwards the deserialized message payload to <see cref="OnMessage"/>; a no-op if no handler is attached yet.</summary>
    public void Dispatch(ChannelMessage message) => OnMessage?.Invoke(message);

    /// <summary>Forwards the deserialized chunk payload to <see cref="OnChunk"/>; a no-op if no handler is attached yet.</summary>
    public void DispatchChunk(StreamChunkRequest chunk) => OnChunk?.Invoke(chunk);
}