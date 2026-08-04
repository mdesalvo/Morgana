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
    /// <summary>
    /// The registry itself: one handshake record per live conversation, keyed by conversation id.
    /// In-memory only — a process restart loses it, and the persisted copy in the conversation's
    /// own database is what a resume replays into it.
    /// </summary>
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