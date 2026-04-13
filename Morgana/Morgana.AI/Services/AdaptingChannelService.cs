using System.Collections.Concurrent;
using Morgana.AI.Adapters;
using Morgana.AI.Interfaces;

namespace Morgana.AI.Services;

/// <summary>
/// Decorator over a concrete <see cref="IChannelService"/> that routes every outbound
/// <see cref="Records.ChannelMessage"/> through <see cref="MorganaChannelAdapter"/> before
/// handing it to the underlying transport. This is the single chokepoint where rich
/// messages are degraded to fit the capabilities advertised by the originating channel,
/// making it impossible for producers (presenter, supervisor, manager, controller, agents)
/// to emit a payload that the target channel cannot carry.
/// </summary>
/// <remarks>
/// <para><strong>Per-conversation metadata:</strong></para>
/// <para>This service also implements <see cref="IChannelMetadataStore"/>: each conversation's
/// channel metadata (name + capability budget) is registered by <c>ConversationManagerActor</c>
/// at start (or restore) and looked up here on every send. When no entry exists for a conversation
/// (legacy DBs or pre-handshake edge cases) the decorator falls back to the wrapped channel's
/// hard-coded metadata, which for the SignalR/Cauldron channel means full features.</para>
///
/// <para><strong>Why a decorator:</strong></para>
/// <para>Producers keep calling <see cref="IChannelService.SendMessageAsync"/> exactly as
/// before. DI hands them this wrapper instead of the concrete channel, so the degradation
/// gate is applied uniformly and any future channel implementation inherits the gate for free.</para>
///
/// <para><strong>Streaming:</strong></para>
/// <para><see cref="SendStreamChunkAsync"/> is a straight pass-through: streaming is gated
/// upstream in <c>MorganaAgent</c> (no streaming connection is ever opened toward a
/// non-streaming channel), so the adapter never sees partial chunks.</para>
///
/// <para><strong>Reliability:</strong></para>
/// <para><see cref="MorganaChannelAdapter.AdaptAsync"/> never throws — worst case it returns
/// a template-based plain rendering — so the decorator adds no new failure modes on the send path.</para>
/// </remarks>
public class AdaptingChannelService : IChannelService, IChannelMetadataStore
{
    /// <summary>
    /// The concrete channel being decorated (e.g. <c>SignalRChannelService</c>). All calls
    /// are ultimately delegated to this instance after (optional) adaptation.
    /// </summary>
    private readonly IChannelService channelService;

    /// <summary>
    /// Adapter responsible for transcoding a rich message into a form that fits the
    /// capabilities of the originating channel. Invoked once per <see cref="SendMessageAsync"/>
    /// call; short-circuits without I/O when the message already fits.
    /// </summary>
    private readonly MorganaChannelAdapter channelAdapter;

    /// <summary>
    /// In-memory registry of per-conversation channel metadata, populated at conversation
    /// start by <c>ConversationManagerActor</c> via <see cref="IChannelMetadataStore.RegisterChannelMetadata"/>.
    /// </summary>
    private readonly ConcurrentDictionary<string, Records.ChannelMetadata> metadataByConversation = new();

    /// <summary>
    /// Initialises a new instance of <see cref="AdaptingChannelService"/>.
    /// </summary>
    /// <param name="channelService">The concrete channel service being decorated.</param>
    /// <param name="channelAdapter">The adapter used to degrade outbound messages to the
    /// capabilities advertised by the originating channel.</param>
    public AdaptingChannelService(IChannelService channelService, MorganaChannelAdapter channelAdapter)
    {
        this.channelService = channelService;
        this.channelAdapter = channelAdapter;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Returns the wrapped channel's static metadata. Per-conversation metadata is served
    /// via <see cref="IChannelMetadataStore.TryGetChannelMetadata"/> instead — consumers
    /// needing the per-turn entry should look up the store keyed by conversation id.
    /// </remarks>
    public Records.ChannelMetadata Metadata => channelService.Metadata;

    /// <inheritdoc/>
    public async Task SendMessageAsync(Records.ChannelMessage message)
    {
        Records.ChannelCapabilities effectiveCapabilities =
            metadataByConversation.TryGetValue(message.ConversationId, out Records.ChannelMetadata? registered)
                ? registered.Capabilities
                : channelService.Metadata.Capabilities;

        Records.ChannelMessage adapted = await channelAdapter.AdaptAsync(message, effectiveCapabilities);
        await channelService.SendMessageAsync(adapted);
    }

    /// <inheritdoc/>
    public Task SendStreamChunkAsync(string conversationId, string chunkText) =>
        channelService.SendStreamChunkAsync(conversationId, chunkText);

    /// <inheritdoc/>
    public void RegisterChannelMetadata(string conversationId, Records.ChannelMetadata metadata) =>
        metadataByConversation[conversationId] = metadata;

    /// <inheritdoc/>
    public void UnregisterChannelMetadata(string conversationId) =>
        metadataByConversation.TryRemove(conversationId, out _);

    /// <inheritdoc/>
    public bool TryGetChannelMetadata(string conversationId, out Records.ChannelMetadata metadata)
    {
        if (metadataByConversation.TryGetValue(conversationId, out Records.ChannelMetadata? value))
        {
            metadata = value;
            return true;
        }

        metadata = channelService.Metadata;
        return false;
    }
}
