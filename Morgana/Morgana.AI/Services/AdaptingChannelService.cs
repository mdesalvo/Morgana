using Microsoft.Extensions.Configuration;
using Morgana.AI.Adapters;
using Morgana.AI.Interfaces;
using Morgana.Contracts;

namespace Morgana.AI.Services;

/// <summary>
/// The channel every part of Morgana sends through: it fits each message to the conversation's capabilities, then hands it to the transport.
/// The transport is chosen per message by the <c>deliveryMode</c> that the conversation announced at the handshake.
/// </summary>
public class AdaptingChannelService : IChannelService
{
    /// <summary>Finds the transport that serves a conversation's <c>deliveryMode</c>.</summary>
    private readonly IChannelServiceFactory channelServiceFactory;

    /// <summary>
    /// The conversation's handshake, read on every send for its capabilities and its delivery mode.
    /// A leaf singleton beside this decorator rather than inside it, which keeps the transports that read it free of a DI cycle.
    /// </summary>
    private readonly IChannelMetadataStore channelMetadataStore;

    /// <summary>Fits a rich message to the channel's capabilities, with no work when it already fits.</summary>
    private readonly MorganaChannelAdapter channelAdapter;

    /// <summary>Seconds that every channel is told to wait on a silent turn.</summary>
    private readonly int replyTimeoutSeconds;

    /// <summary>Binds the decorator to the transports, the handshakes and the adapter, reading the wait that every channel is told.</summary>
    public AdaptingChannelService(
        IChannelServiceFactory channelServiceFactory,
        IChannelMetadataStore channelMetadataStore,
        MorganaChannelAdapter channelAdapter,
        IConfiguration configuration)
    {
        this.channelServiceFactory = channelServiceFactory;
        this.channelMetadataStore = channelMetadataStore;
        this.channelAdapter = channelAdapter;

        // The wait that every channel is told, read once from the ladder that orders every party's wait.
        replyTimeoutSeconds = (int)Records.TurnWaits.From(configuration).Channel.TotalSeconds;
    }

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">The message's conversation has no handshake on record.</exception>
    public async Task SendMessageAsync(ChannelMessage channelMessage)
    {
        // One lookup serving both concerns below: capabilities decide what the message becomes,
        // coordinates decide who carries it.
        ChannelMetadata registeredChannelMetadata = await channelMetadataStore.GetChannelMetadataAsync(channelMessage.ConversationId);

        // Every message to a channel tells it how long to wait on its next turn whichever part of Morgana wrote
        // the message and the dust level it already carries stays.
        ChannelMessage stampedChannelMessage = channelMessage with
        {
            ConversationMetadata = (channelMessage.ConversationMetadata ?? new ConversationMetadata()) with { ReplyTimeoutSeconds = replyTimeoutSeconds }
        };

        // Adapt before resolving the transport, never after: what the transport is handed must already
        // fit the channel and AdaptAsync short-circuits without I/O when it already does.
        ChannelMessage adaptedChannelMessage = await channelAdapter.AdaptAsync(stampedChannelMessage, registeredChannelMetadata.Capabilities);

        // Resolved per send rather than held: the same decorator serves every conversation and each
        // announced its own delivery mode at the handshake.
        IChannelService concreteChannelService = channelServiceFactory.Resolve(registeredChannelMetadata.Coordinates.DeliveryMode);

        // Puts the message on the channel — SignalR to the conversation's group, webhook to its
        // callback — already within the budget that channel declared.
        await concreteChannelService.SendMessageAsync(adaptedChannelMessage);

        // The message as the user received it becomes the conversation's last answer, whose buttons a press of the
        // next message is recognised on. Kept only once the transport accepted it, so an undelivered message never counts.
        await channelMetadataStore.RecordDeliveredMessageAsync(adaptedChannelMessage);
    }

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException"><paramref name="conversationId"/> has no handshake on record.</exception>
    public async Task SendStreamChunkAsync(string conversationId, string chunkText)
    {
        // Read for its coordinates alone: a chunk is a fragment of still-forming text, so the
        // capability budget the same record carries has nothing here to act on.
        ChannelMetadata registeredChannelMetadata = await channelMetadataStore.GetChannelMetadataAsync(conversationId);

        // The route the conversation announced at the handshake, looked up again per chunk rather
        // than held: one decorator serves every conversation.
        IChannelService concreteChannelService = channelServiceFactory.Resolve(registeredChannelMetadata.Coordinates.DeliveryMode);

        // Puts the fragment on the channel, which appends it to the message taking shape on screen.
        await concreteChannelService.SendStreamChunkAsync(conversationId, chunkText);
    }
}