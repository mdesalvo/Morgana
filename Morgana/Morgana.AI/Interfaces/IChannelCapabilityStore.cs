namespace Morgana.AI.Interfaces;

/// <summary>
/// In-process registry of per-conversation <see cref="Records.ChannelCapabilities"/>.
/// Owned by the same singleton that decorates the outbound channel
/// (<c>AdaptingChannelService</c>) so the decorator can look up the capability budget
/// to apply when degrading an outbound <see cref="Records.ChannelMessage"/>, while
/// producers (typically <c>ConversationManagerActor</c>) register and unregister entries
/// at conversation start / end.
/// </summary>
/// <remarks>
/// <para><strong>Lifecycle:</strong></para>
/// <list type="bullet">
/// <item><term>RegisterChannelCapabilities</term><description>Called by <c>ConversationManagerActor</c> after the capability handshake (or after restoring a persisted set on resume), once per conversation.</description></item>
/// <item><term>TryGetChannelCapabilities</term><description>Called by the decorator on every outbound send and by <c>ConversationSupervisorActor</c> when constructing per-turn <c>AgentRequest</c>.</description></item>
/// <item><term>UnregisterChannelCapabilities</term><description>Called by <c>ConversationManagerActor</c> on conversation end / actor stop to release the entry.</description></item>
/// </list>
/// <para><strong>Why not actor-resident state:</strong></para>
/// <para>The decorator lives in DI as a singleton and cannot ask Akka for an actor reference
/// on the hot send path. Keeping a process-wide registry keyed by conversation id is the
/// simplest way to give the decorator O(1) access without breaking the existing actor model.</para>
/// </remarks>
public interface IChannelCapabilityStore
{
    /// <summary>
    /// Registers (or replaces) the capability budget for a conversation.
    /// </summary>
    void RegisterChannelCapabilities(string conversationId, Records.ChannelCapabilities capabilities);

    /// <summary>
    /// Removes the capability entry for a conversation. No-op if no entry exists.
    /// </summary>
    void UnregisterChannelCapabilities(string conversationId);

    /// <summary>
    /// Looks up the capability budget for a conversation.
    /// </summary>
    /// <returns>True when an entry exists; false otherwise (callers should fall back to the channel's hard-coded full capabilities).</returns>
    bool TryGetChannelCapabilities(string conversationId, out Records.ChannelCapabilities capabilities);
}
