using Morgana.Contracts;

namespace Morgana.Terminal.Services;

/// <summary>
/// Hands what Morgana POSTs to the webhook endpoints over to the console UI, for the conversation on screen only.
/// The lifecycle wires OnMessage and OnChunk once both this receiver and the UI are resolved, which keeps a circular
/// dependency out of the constructors.
/// </summary>
public sealed class WebhookReceiverService
{
    /// <summary>Backing store of <see cref="ExpectedConversationId"/>, written by the lifecycle and read by Kestrel's request threads.</summary>
    private volatile string? expectedConversationId;

    /// <summary>
    /// The only conversation whose deliveries reach the UI; null accepts none.
    /// The callback URL outlives a conversation, so a delivery for another one, such as an abandoned start attempt or a
    /// previous run on the same port, must not land on screen.
    /// </summary>
    public string? ExpectedConversationId
    {
        get => expectedConversationId;
        set => expectedConversationId = value;
    }

    /// <summary>Receives every accepted message, wired to the UI's inbound queue.</summary>
    public Action<ChannelMessage>? OnMessage { get; set; }

    /// <summary>Receives every accepted stream chunk; unwired on a channel that declares no streaming, which then turns every chunk away.</summary>
    public Action<StreamChunkRequest>? OnChunk { get; set; }

    /// <summary>Forwards the message to <see cref="OnMessage"/> when it belongs to the expected conversation and tells whether it did.</summary>
    public bool Dispatch(ChannelMessage message)
    {
        // A delivery for any other conversation is refused, which the endpoint answers with 404.
        if (!IsExpected(message.ConversationId))
            return false;

        // The UI queue receives the accepted message.
        OnMessage?.Invoke(message);
        return true;
    }

    /// <summary>Forwards the chunk to <see cref="OnChunk"/> when it belongs to the expected conversation and the channel renders chunks, telling whether it did.</summary>
    public bool DispatchChunk(StreamChunkRequest chunk)
    {
        // A channel that shows no chunks refuses them all, as it refuses a chunk of another conversation.
        if (OnChunk is not { } onChunk || !IsExpected(chunk.ConversationId))
            return false;

        // The UI queue receives the accepted chunk.
        onChunk(chunk);
        return true;
    }

    /// <summary>Tells whether a delivery addressed to <paramref name="conversationId"/> belongs to the conversation on screen.</summary>
    private bool IsExpected(string? conversationId) =>
        expectedConversationId is { } expected && string.Equals(conversationId, expected, StringComparison.Ordinal);
}