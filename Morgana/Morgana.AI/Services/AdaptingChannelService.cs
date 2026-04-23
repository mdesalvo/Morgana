using Morgana.AI.Adapters;
using Morgana.AI.Interfaces;

namespace Morgana.AI.Services;

/// <summary>
/// Top-level <see cref="IChannelService"/> that every actor and service binds to. Responsible
/// for two orthogonal concerns on every outbound send: (1) adapting the payload to the
/// capabilities advertised by the originating channel via <see cref="MorganaChannelAdapter"/>,
/// and (2) dispatching the adapted payload to the concrete transport registered for the
/// conversation's <c>deliveryMode</c> via <see cref="IChannelServiceFactory"/>.
/// </summary>
/// <remarks>
/// <para><strong>Per-conversation metadata:</strong></para>
/// <para>The per-conversation metadata (name + delivery mode + capability budget) is owned by a
/// separate leaf singleton (<see cref="IChannelMetadataStore"/>) which this service reads on
/// every send. Keeping the store out of this type's identity is a deliberate DI decision:
/// concrete transports like <c>WebhookChannelService</c> need the store to resolve their
/// addressing, and folding it back into the decorator would close a cycle
/// (<c>IChannelServiceFactory → WebhookChannelService → IChannelMetadataStore → this → IChannelServiceFactory</c>)
/// that the container cannot break. A missing entry at send-time is always an invariant
/// violation — the start-conversation gate and the manager's registration step guarantee the
/// entry is in place before any producer emits.</para>
///
/// <para><strong>Why adapt + dispatch in one place:</strong></para>
/// <para>Producers (presenter, supervisor, manager, controller, agents) keep calling
/// <see cref="IChannelService.SendMessageAsync"/> exactly as before: DI hands them this
/// instance, so the degradation gate and the transport selection are both applied uniformly.
/// Any new channel implementation inherits the gate for free just by being registered with its
/// own <c>deliveryMode</c> key via <see cref="ChannelServiceRegistration"/>.</para>
///
/// <para><strong>Streaming:</strong></para>
/// <para><see cref="SendStreamChunkAsync"/> skips the adapter (chunks are partial, not structured)
/// but still dispatches through the factory so the chunk reaches the right transport. Streaming
/// is gated upstream in <c>MorganaAgent</c> / <c>ConversationManagerActor</c> (no streaming
/// connection is ever opened toward a non-streaming channel), so a dispatch here only happens
/// when the concrete transport can carry chunks.</para>
///
/// <para><strong>Reliability:</strong></para>
/// <para><see cref="MorganaChannelAdapter.AdaptAsync"/> never throws — worst case it returns
/// a template-based plain rendering — so the adaptation step adds no new failure modes on the
/// send path. Dispatch failures surface from the concrete transport.</para>
/// </remarks>
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
    public async Task SendMessageAsync(Records.ChannelMessage channelMessage)
    {
        Records.ChannelMetadata registeredChannelMetadata = GetRegisteredMetadataOrThrow(channelMessage.ConversationId);

        Records.ChannelMessage adaptedChannelMessage = await channelAdapter.AdaptAsync(channelMessage, registeredChannelMetadata.Capabilities);
        IChannelService concreteChannelService = channelServiceFactory.Resolve(registeredChannelMetadata.Coordinates.DeliveryMode);
        await concreteChannelService.SendMessageAsync(adaptedChannelMessage);
    }

    /// <inheritdoc/>
    public Task SendStreamChunkAsync(string conversationId, string chunkText)
    {
        Records.ChannelMetadata registeredChannelMetadata = GetRegisteredMetadataOrThrow(conversationId);
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
    private Records.ChannelMetadata GetRegisteredMetadataOrThrow(string conversationId)
    {
        if (!channelMetadataStore.TryGetChannelMetadata(conversationId, out Records.ChannelMetadata? registeredChannelMetadata))
            throw new InvalidOperationException(
                $"No channel metadata registered for conversation {conversationId}; " +
                "the start-conversation gate should have ensured registration before any send.");
        return registeredChannelMetadata;
    }
}
