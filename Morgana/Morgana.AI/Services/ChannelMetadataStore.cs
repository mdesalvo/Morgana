using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Morgana.AI.Interfaces;

namespace Morgana.AI.Services;

/// <summary>
/// Default <see cref="IChannelMetadataStore"/> implementation: a process-wide
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> keyed by conversation id.
/// </summary>
/// <remarks>
/// <para>Kept as a standalone leaf singleton — with no dependency on any channel service — so
/// that concrete transports (e.g. <c>WebhookChannelService</c>) can read per-conversation
/// coordinates without pulling the <c>AdaptingChannelService</c> into their construction
/// graph. Merging the store into the decorator would close a cycle
/// (<c>IChannelServiceFactory → WebhookChannelService → IChannelMetadataStore → AdaptingChannelService → IChannelServiceFactory</c>)
/// that the DI container cannot resolve.</para>
/// </remarks>
public class ChannelMetadataStore : IChannelMetadataStore
{
    private readonly ConcurrentDictionary<string, Records.ChannelMetadata> metadataByConversation = new();

    /// <inheritdoc/>
    public void RegisterChannelMetadata(string conversationId, Records.ChannelMetadata channelMetadata) =>
        metadataByConversation[conversationId] = channelMetadata;

    /// <inheritdoc/>
    public void UnregisterChannelMetadata(string conversationId) =>
        metadataByConversation.TryRemove(conversationId, out _);

    /// <inheritdoc/>
    public bool TryGetChannelMetadata(string conversationId, [NotNullWhen(true)] out Records.ChannelMetadata? channelMetadata) =>
        metadataByConversation.TryGetValue(conversationId, out channelMetadata);
}