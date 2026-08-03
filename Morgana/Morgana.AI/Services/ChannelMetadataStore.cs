using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Morgana.AI.Interfaces;
using Morgana.Contracts;

namespace Morgana.AI.Services;

/// <summary>
/// Process-wide <see cref="ConcurrentDictionary{TKey,TValue}"/> keyed by conversation id. A leaf
/// singleton with no channel-service dependency, so transports can read it without a DI cycle.
/// </summary>
public class ChannelMetadataStore : IChannelMetadataStore
{
    private readonly ConcurrentDictionary<string, ChannelMetadata> metadataByConversation = new();

    /// <inheritdoc/>
    // Plain indexer assignment, not TryAdd: registering the same conversationId twice (e.g. a
    // resume that re-announces ChannelMetadata) is meant to overwrite, not fail — last write wins.
    public void RegisterChannelMetadata(string conversationId, ChannelMetadata channelMetadata) =>
        metadataByConversation[conversationId] = channelMetadata;

    /// <inheritdoc/>
    public void UnregisterChannelMetadata(string conversationId) =>
        metadataByConversation.TryRemove(conversationId, out _);

    /// <inheritdoc/>
    public bool TryGetChannelMetadata(string conversationId, [NotNullWhen(true)] out ChannelMetadata? channelMetadata) =>
        metadataByConversation.TryGetValue(conversationId, out channelMetadata);
}