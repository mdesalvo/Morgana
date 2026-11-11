using Morgana.Contracts;

namespace Morgana.AI.Interfaces;

/// <summary>The one owner of each conversation's channel, settled at the handshake and read by every outbound path.</summary>
// The conversation database is the record and memory only a copy of it: after a restart the first message may reach a
// process that never saw the handshake, so a lookup that misses in memory is answered from the record. The store alone
// turns what a channel declares into what Morgana will send it and refuses a missing handshake, so no reader applies a
// channel rule of its own. A leaf singleton, because the adapting decorator cannot ask Akka for an actor on the send path
// and folding the store into it would close a DI cycle with the transports that read it too.
public interface IChannelMetadataStore
{
    /// <summary>Settles the handshake of a conversation being started and returns the channel as every reader will see it.</summary>
    // What the channel declared is normalised into what Morgana will send it, persisted and kept in memory, so the conversation
    // exists on record once this returns. A handshake that cannot be persisted fails the call, because nothing else of the
    // conversation could be recorded either.
    Task<ChannelMetadata> RegisterChannelMetadataAsync(string conversationId, ChannelMetadata declaredChannelMetadata);

    /// <summary>Drops the copy in memory of a conversation's handshake and last answer, leaving the record for a later lookup.</summary>
    void EvictChannelMetadata(string conversationId);

    /// <summary>
    /// The channel of a conversation, from memory or else from its record.
    /// Every send asks, stream chunks included, so past the first lookup the answer comes from memory without I/O.
    /// </summary>
    /// <exception cref="InvalidOperationException">The conversation never completed a handshake, which the start-conversation gate makes a fault rather than a case to serve.</exception>
    ValueTask<ChannelMetadata> GetChannelMetadataAsync(string conversationId);

    /// <summary>Keeps a delivered answer or presentation as its conversation's last answer, ignoring notices.</summary>
    ValueTask RecordDeliveredMessageAsync(ChannelMessage deliveredMessage);

    /// <summary>The last answer that this process delivered to a conversation as the user saw it; null when none was delivered since start or eviction.</summary>
    // The default store holds it in memory with no record behind it, while a store kept outside the process answers over I/O.
    ValueTask<ChannelMessage?> GetLastAnswerAsync(string conversationId);
}