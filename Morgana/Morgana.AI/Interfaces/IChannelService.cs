using Morgana.Contracts;

namespace Morgana.AI.Interfaces;

/// <summary>
/// Delivers Morgana's messages to the end user through the transport of a conversation's channel.
/// Actors emit messages without knowing whether SignalR, a webhook or any other transport carries them. The interface
/// exposes no channel identity or capability budget: those belong to the client that announced itself at the handshake,
/// not to the transport that carries the bytes, so every reader takes them from IChannelMetadataStore.
/// </summary>
public interface IChannelService
{
    /// <summary>Delivers <paramref name="channelMessage"/> to the conversation that its <see cref="ChannelMessage.ConversationId"/> names.</summary>
    Task SendMessageAsync(ChannelMessage channelMessage);

    /// <summary>
    /// Delivers one streamed piece of an answer being written, appended by the channel to the answer in progress.
    /// The caller gates the call on the conversation's capabilities, so a transport that cannot carry streaming may do nothing.
    /// </summary>
    /// <param name="conversationId">The conversation whose answer is being streamed.</param>
    /// <param name="chunkText">The text that this piece adds to the answer.</param>
    Task SendStreamChunkAsync(string conversationId, string chunkText);
}