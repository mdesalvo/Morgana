using Morgana.Contracts;

namespace Morgana.AI.Interfaces;

/// <summary>
/// The channel of each conversation: coordinates plus capability budget, settled once at the
/// handshake. Every outbound path reads it: the adapting decorator, the concrete transports, the
/// presenter and the supervisor stamping per-turn agent requests.
/// </summary>
/// <remarks>
/// <para><strong>The conversation database is the record, memory only a copy of it.</strong> A
/// conversation outlives the process serving it: after a restart the first message may reach a host
/// that never saw its handshake, through a client that only reconnected its transport. A lookup that
/// misses in memory is therefore answered from the persisted handshake, so no caller depends on
/// which endpoint happened to reach this process first.</para>
/// <para><strong>The store alone decides what a channel gets.</strong> What a channel declares is
/// turned into what Morgana will send it once, at registration, then persisted in that form: no
/// reader applies a channel rule of its own. Likewise a missing handshake is refused here and never
/// restated by a reader, since none of them has a fallback.</para>
/// <para><strong>Why a leaf singleton:</strong> the decorator lives in DI and cannot ask Akka for an
/// actor reference on the hot send path, while folding the registry into it would close a DI cycle
/// with the transports that read it too.</para>
/// </remarks>
public interface IChannelMetadataStore
{
    /// <summary>
    /// Settles the handshake of a conversation being started. What the channel declared is
    /// normalised into what Morgana will send it, persisted in the conversation's database and kept
    /// in memory. Once this returns the conversation exists on record.
    /// </summary>
    /// <returns>The channel as every reader will see it from now on.</returns>
    /// <remarks>A handshake that cannot be persisted fails the call: the conversation does not exist
    /// and the start must fail, since nothing else of it could be recorded either.</remarks>
    Task<ChannelMetadata> RegisterChannelMetadataAsync(string conversationId, ChannelMetadata declaredChannelMetadata);

    /// <summary>
    /// Drops the in-memory copy of a conversation's handshake. The persisted record stays, so a
    /// later lookup for the same conversation still finds it.
    /// </summary>
    void EvictChannelMetadata(string conversationId);

    /// <summary>
    /// Returns the channel of a conversation, from memory or else from its database. Every send
    /// asks, stream chunks included: past the first lookup the answer is in memory, with no I/O.
    /// </summary>
    /// <exception cref="InvalidOperationException">The conversation never completed a handshake:
    /// the start-conversation gate refuses to open one without it, so this is a fault to surface
    /// and never a case to serve.</exception>
    ValueTask<ChannelMetadata> GetChannelMetadataAsync(string conversationId);
}
