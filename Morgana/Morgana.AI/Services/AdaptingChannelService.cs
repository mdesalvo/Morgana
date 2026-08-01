using Morgana.AI.Adapters;
using Morgana.AI.Interfaces;
using Morgana.Contracts;

namespace Morgana.AI.Services;

/// <summary>
/// Decorator IChannelService applying two concerns per send: (1) adapt via MorganaChannelAdapter to channel capabilities,
/// (2) dispatch via IChannelServiceFactory to concrete transport (by deliveryMode). Per-conversation metadata (name + mode +
/// budget) owned by leaf singleton IChannelMetadataStore (deliberate DI choice: breaks cycle if folded into decorator).
/// SendStreamChunkAsync skips adapter (chunks partial, not structured) but still dispatches. AdaptAsync never throws.
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

    /// <summary>
    /// Initialises a new instance of <see cref="AdaptingChannelService"/>.
    /// </summary>
    /// <param name="channelServiceFactory">Factory that resolves the concrete
    /// <see cref="IChannelService"/> for a conversation's delivery mode.</param>
    /// <param name="channelMetadataStore">Registry from which per-conversation channel metadata
    /// is read on every send.</param>
    /// <param name="channelAdapter">The adapter used to degrade outbound messages to the
    /// capabilities advertised by the originating channel.</param>
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
    public async Task SendMessageAsync(ChannelMessage channelMessage)
    {
        ChannelMetadata registeredChannelMetadata = GetRegisteredMetadataOrThrow(channelMessage.ConversationId);

        ChannelMessage adaptedChannelMessage = await channelAdapter.AdaptAsync(channelMessage, registeredChannelMetadata.Capabilities);
        IChannelService concreteChannelService = channelServiceFactory.Resolve(registeredChannelMetadata.Coordinates.DeliveryMode);
        await concreteChannelService.SendMessageAsync(adaptedChannelMessage);
    }

    /// <inheritdoc/>
    public Task SendStreamChunkAsync(string conversationId, string chunkText)
    {
        ChannelMetadata registeredChannelMetadata = GetRegisteredMetadataOrThrow(conversationId);
        IChannelService concreteChannelService = channelServiceFactory.Resolve(registeredChannelMetadata.Coordinates.DeliveryMode);
        return concreteChannelService.SendStreamChunkAsync(conversationId, chunkText);
    }

    /// <summary>
    /// Looks up the registered channel metadata for a conversation or throws if none is
    /// registered. The start-conversation gate in MorganaController refuses handshakes without
    /// channel metadata, and ConversationManagerActor registers the per-conversation entry
    /// before any outbound send happens — reaching a send path without a registered entry is
    /// therefore an internal invariant violation, not a client mistake.
    /// </summary>
    private ChannelMetadata GetRegisteredMetadataOrThrow(string conversationId)
    {
        if (!channelMetadataStore.TryGetChannelMetadata(conversationId, out ChannelMetadata? registeredChannelMetadata))
            throw new InvalidOperationException(
                $"No channel metadata registered for conversation {conversationId}; " +
                "the start-conversation gate should have ensured registration before any send.");
        return registeredChannelMetadata;
    }
}
