using System.Diagnostics.CodeAnalysis;

namespace Morgana.AI.Interfaces;

/// <summary>
/// In-process registry of per-conversation <see cref="Records.ChannelMetadata"/> (channel
/// name plus capability budget). Owned by the same singleton that decorates the outbound
/// channel (<c>AdaptingChannelService</c>) so the decorator can look up the metadata to
/// apply when degrading an outbound <see cref="Records.ChannelMessage"/>, while producers
/// (typically <c>ConversationManagerActor</c>) register and unregister entries at
/// conversation start / end.
/// </summary>
/// <remarks>
/// <para><strong>Lifecycle:</strong></para>
/// <list type="bullet">
/// <item><term>RegisterChannelMetadata</term><description>Called by <c>ConversationManagerActor</c> after the channel handshake (or after restoring a persisted entry on resume), once per conversation.</description></item>
/// <item><term>TryGetChannelMetadata</term><description>Called by the decorator on every outbound send and by <c>ConversationSupervisorActor</c> when constructing per-turn <c>AgentRequest</c>.</description></item>
/// <item><term>UnregisterChannelMetadata</term><description>Called by <c>ConversationManagerActor</c> on conversation end / actor stop to release the entry.</description></item>
/// </list>
/// <para><strong>Why not actor-resident state:</strong></para>
/// <para>The decorator lives in DI as a singleton and cannot ask Akka for an actor reference
/// on the hot send path. Keeping a process-wide registry keyed by conversation id is the
/// simplest way to give the decorator O(1) access without breaking the existing actor model.</para>
/// </remarks>
public interface IChannelMetadataStore
{
    /// <summary>
    /// Registers (or replaces) the channel metadata for a conversation.
    /// </summary>
    void RegisterChannelMetadata(string conversationId, Records.ChannelMetadata channelMetadata);

    /// <summary>
    /// Removes the metadata entry for a conversation. No-op if no entry exists.
    /// </summary>
    void UnregisterChannelMetadata(string conversationId);

    /// <summary>
    /// Looks up the channel metadata for a conversation.
    /// </summary>
    /// <returns>True when an entry exists; false otherwise. A false result indicates that the
    /// controller gate or the manager registration step was bypassed and callers must treat
    /// this as an invariant violation — there is no transport-level default to fallback to.</returns>
    bool TryGetChannelMetadata(string conversationId, [NotNullWhen(true)] out Records.ChannelMetadata? channelMetadata);
}
