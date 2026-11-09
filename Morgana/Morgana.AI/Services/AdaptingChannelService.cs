using Microsoft.Extensions.Configuration;
using Morgana.AI.Adapters;
using Morgana.AI.Interfaces;
using Morgana.Contracts;

namespace Morgana.AI.Services;

/// <summary>
/// Decorates IChannelService by applying two concerns per send: (1) adapt via MorganaChannelAdapter to channel capabilities,
/// (2) dispatch via IChannelServiceFactory to concrete transport (by deliveryMode). Per-conversation metadata (name + mode +
/// budget) owned by leaf singleton IChannelMetadataStore (deliberate DI choice: breaks cycle if folded into decorator).
/// </summary>
public class AdaptingChannelService : IChannelService
{
    /// <summary>
    /// Factory that maps a conversation's <c>deliveryMode</c> to the concrete
    /// <see cref="IChannelService"/> that carries its bytes. Populated at DI registration with
    /// one <see cref="ChannelServiceRegistration"/> per transport.
    /// </summary>
    private readonly IChannelServiceFactory channelServiceFactory;

    /// <summary>
    /// Per-conversation channel metadata, recorded at the handshake and queried here on every send
    /// to recover the capability budget and delivery mode for the outgoing conversation.
    /// </summary>
    private readonly IChannelMetadataStore channelMetadataStore;

    /// <summary>
    /// Adapter responsible for transcoding a rich message into a form that fits the
    /// capabilities of the originating channel. Invoked once per <see cref="SendMessageAsync"/>
    /// call; short-circuits without I/O when the message already fits.
    /// </summary>
    private readonly MorganaChannelAdapter channelAdapter;

    /// <summary>Seconds that every channel is told to wait on a silent turn.</summary>
    private readonly int replyTimeoutSeconds;

    /// <param name="channelServiceFactory">Resolves the concrete transport for a conversation's <c>deliveryMode</c>.</param>
    /// <param name="channelMetadataStore">Leaf singleton holding the per-conversation handshake; injected rather than folded in, to keep the DI graph acyclic.</param>
    /// <param name="channelAdapter">Capability-driven degradation applied to every outbound <see cref="ChannelMessage"/>.</param>
    /// <param name="configuration">Application configuration, read for the turn budget that the channels' wait derives from.</param>
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