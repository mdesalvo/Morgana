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
    // No AdaptAsync call here, unlike SendMessageAsync: a stream chunk is a fragment of
    // still-forming text, not a structured ChannelMessage — there's nothing coherent yet for the
    // adapter to degrade. Streaming is suppressed upstream whenever adaptation would be needed
    // (see ConversationSupervisorActor.GetEffectiveCapabilities), so this path only ever runs for
    // channels the message will reach unadapted anyway.
    public Task SendStreamChunkAsync(string conversationId, string chunkText)
    {
        ChannelMetadata registeredChannelMetadata = GetRegisteredMetadataOrThrow(conversationId);
        IChannelService concreteChannelService = channelServiceFactory.Resolve(registeredChannelMetadata.Coordinates.DeliveryMode);
        return concreteChannelService.SendStreamChunkAsync(conversationId, chunkText);
    }

    /// <summary>
    /// Looks up the registered channel metadata or throws. A miss here is an internal invariant
    /// violation, not a client mistake — the start-conversation gate and ConversationManagerActor
    /// guarantee registration before any send path can be reached.
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
