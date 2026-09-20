using Morgana.Contracts;

namespace Morgana.Terminal.Interfaces;

/// <summary>
/// The live terminal UI of a channel, as the conversation lifecycle needs to see it: somewhere to
/// queue what Morgana delivers, and something to hand the terminal over to for the length of the
/// conversation. How any of it is drawn is the channel's business.
/// </summary>
public interface ITerminalUi
{
    /// <summary>Queues a delivery from Morgana for display.</summary>
    void EnqueueIncoming(ChannelMessage message);

    /// <summary>
    /// Where incremental chunks are queued. A channel that declares no streaming leaves this unset,
    /// and the chunks it would never show are turned away at the webhook.
    /// </summary>
    Action<StreamChunkRequest>? ChunkSink => null;

    /// <summary>
    /// Owns the terminal until the user quits or <paramref name="cancellationToken"/> fires, sending
    /// whatever the user types through <paramref name="onSend"/>.
    /// </summary>
    Task RunAsync(string conversationId, Func<string, Task> onSend, CancellationToken cancellationToken = default);
}