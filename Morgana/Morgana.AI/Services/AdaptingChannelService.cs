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
    /// Registry of per-conversation channel metadata, populated by <c>ConversationManagerActor</c>
    /// at handshake and queried here on every send to recover the capability budget and delivery
    /// mode for the outgoing conversation.
    /// </summary>
    private readonly IChannelMetadataStore channelMetadataStore;

    /// <summary>
    /// Adapter responsible for transcoding a rich message into a form that fits the
    /// capabilities of the originating channel. Invoked once per <see cref="SendMessageAsync"/>
    /// call; short-circuits without I/O when the message already fits.
    /// </summary>
    private readonly MorganaChannelAdapter channelAdapter;

    /// <param name="channelServiceFactory">Resolves the concrete transport for a conversation's <c>deliveryMode</c>.</param>
    /// <param name="channelMetadataStore">Leaf singleton holding the per-conversation handshake; injected rather than folded in, to keep the DI graph acyclic.</param>
    /// <param name="channelAdapter">Capability-driven degradation applied to every outbound <see cref="ChannelMessage"/>.</param>
    public AdaptingChannelService(
        IChannelServiceFactory channelServiceFactory,
        IChannelMetadataStore channelMetadataStore,
        MorganaChannelAdapter channelAdapter)
    {
        this.channelServiceFactory = channelServiceFactory;
        this.channelMetadataStore = channelMetadataStore;
        this.channelAdapter = channelAdapter;
    }

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">
    /// No channel metadata is registered for the message's conversation — an internal invariant
    /// violation, see <see cref="GetRegisteredMetadataOrThrow"/>.
    /// </exception>
    public async Task SendMessageAsync(ChannelMessage channelMessage)
    {
        // One lookup serving both concerns below: capabilities decide what the message becomes,
        // coordinates decide who carries it.
        ChannelMetadata registeredChannelMetadata = GetRegisteredMetadataOrThrow(channelMessage.ConversationId);

        // Adapt before resolving the transport, never after: what the transport is handed must already
        // fit the channel and AdaptAsync short-circuits without I/O when it already does.
        ChannelMessage adaptedChannelMessage = await channelAdapter.AdaptAsync(channelMessage, registeredChannelMetadata.Capabilities);

        // Resolved per send rather than held: the same decorator serves every conversation and each
        // announced its own delivery mode at the handshake.
        IChannelService concreteChannelService = channelServiceFactory.Resolve(registeredChannelMetadata.Coordinates.DeliveryMode);

        // Puts the message on the channel — SignalR to the conversation's group, webhook to its
        // callback — already within the budget that channel declared.
        await concreteChannelService.SendMessageAsync(adaptedChannelMessage);
    }

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">
    /// No channel metadata is registered for <paramref name="conversationId"/> — an internal
    /// invariant violation, see <see cref="GetRegisteredMetadataOrThrow"/>.
    /// </exception>
    public Task SendStreamChunkAsync(string conversationId, string chunkText)
    {
        // Read for its coordinates alone: a chunk is a fragment of still-forming text, so the
        // capability budget the same record carries has nothing here to act on.
        ChannelMetadata registeredChannelMetadata = GetRegisteredMetadataOrThrow(conversationId);

        // The route the conversation announced at the handshake, looked up again per chunk rather
        // than held: one decorator serves every conversation.
        IChannelService concreteChannelService = channelServiceFactory.Resolve(registeredChannelMetadata.Coordinates.DeliveryMode);

        // Puts the fragment on the channel, which appends it to the message taking shape on screen.
        return concreteChannelService.SendStreamChunkAsync(conversationId, chunkText);
    }

    /// <summary>
    /// Looks up the registered channel metadata or throws. A miss here is an internal invariant
    /// violation, not a client mistake — the start-conversation gate and ConversationManagerActor
    /// guarantee registration before any send path can be reached.
    /// </summary>
    /// <param name="conversationId">Conversation whose handshake record is looked up.</param>
    /// <returns>The registered metadata: coordinates (for transport resolution) plus capabilities (for adaptation).</returns>
    /// <exception cref="InvalidOperationException">No metadata is registered for the conversation.</exception>
    private ChannelMetadata GetRegisteredMetadataOrThrow(string conversationId)
    {
        // Throwing beats sending blind: with no handshake there is neither a transport to pick nor a
        // budget to degrade against and guessing either would deliver something nobody declared.
        if (!channelMetadataStore.TryGetChannelMetadata(conversationId, out ChannelMetadata? registeredChannelMetadata))
            throw new InvalidOperationException(
                $"No channel metadata registered for conversation {conversationId}; " +
                "the start-conversation gate should have ensured registration before any send.");
        return registeredChannelMetadata;
    }
}