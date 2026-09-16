using Morgana.Contracts;

namespace Grimoire.Services;

/// <summary>
/// Thin dispatcher invoked by the minimal-API <c>POST /morgana-hook</c> endpoint.
/// Decouples the HTTP surface from the console UI: <see cref="OnMessage"/> is wired
/// by <see cref="ConversationLifecycleService"/> after both the receiver and the UI have been
/// resolved from DI, avoiding a constructor-level circular dependency.
/// </summary>
public sealed class WebhookReceiverService
{
    /// <summary>Backing store of <see cref="ExpectedConversationId"/>, written by the lifecycle and read by Kestrel's request threads.</summary>
    private volatile string? expectedConversationId;

    /// <summary>
    /// The only conversation whose deliveries reach the UI; null accepts none. The callback URL outlives
    /// a conversation, so a delivery for another one, such as an abandoned start attempt or a previous
    /// run on the same port, must not land on screen.
    /// </summary>
    public string? ExpectedConversationId
    {
        get => expectedConversationId;
        set => expectedConversationId = value;
    }

    /// <summary>Callback invoked for every accepted message; wired to <see cref="ConsoleUiService.EnqueueIncoming"/>.</summary>
    public Action<ChannelMessage>? OnMessage { get; set; }

    /// <summary>Callback invoked for every accepted stream chunk; wired to <see cref="ConsoleUiService.EnqueueChunk"/>.</summary>
    public Action<StreamChunkRequest>? OnChunk { get; set; }

    /// <summary>Forwards the message to <see cref="OnMessage"/> when it belongs to the expected conversation; returns whether it did.</summary>
    public bool Dispatch(ChannelMessage message)
    {
        if (!IsExpected(message.ConversationId))
            return false;

        OnMessage?.Invoke(message);
        return true;
    }

    /// <summary>Forwards the chunk to <see cref="OnChunk"/> when it belongs to the expected conversation; returns whether it did.</summary>
    public bool DispatchChunk(StreamChunkRequest chunk)
    {
        if (!IsExpected(chunk.ConversationId))
            return false;

        OnChunk?.Invoke(chunk);
        return true;
    }

    /// <summary>Tells whether a delivery addressed to <paramref name="conversationId"/> belongs to the conversation on screen.</summary>
    private bool IsExpected(string? conversationId) =>
        expectedConversationId is { } expected && string.Equals(conversationId, expected, StringComparison.Ordinal);
}
