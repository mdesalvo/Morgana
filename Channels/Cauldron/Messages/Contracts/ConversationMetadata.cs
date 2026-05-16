namespace Cauldron.Messages.Contracts;

/// <summary>
/// <para>
/// Conversation-level metadata carried by every <see cref="ChannelMessage"/>.
/// Mirrors <c>Morgana.AI.Records.ConversationMetadata</c>.
/// </para>
/// <para>
/// CONTRACT VERSION: 1.0<br/>
/// SYNC WITH: Morgana.AI (Records.ConversationMetadata)
/// </para>
/// <para>
/// ⚠️ IMPORTANT: This DTO is duplicated in Morgana. When making changes, update both
/// versions in lockstep and keep backward compatibility (additive, optional fields only).
/// </para>
/// </summary>
/// <remarks>
/// Holds dimensions that characterise the conversation itself, not the message content
/// (remaining dust budget today; active agent, health, … as the framework grows).
/// </remarks>
public sealed class ConversationMetadata
{
    /// <summary>
    /// Ratio of consumed dust to the conversation's budget, 0.0 to &gt;1.0.
    /// Null when dust limiting is disabled — the UI hides the gauge in that case.
    /// </summary>
    public double? DustLevel { get; set; }
}
