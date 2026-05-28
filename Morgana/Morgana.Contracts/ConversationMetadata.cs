namespace Morgana.Contracts;

/// <summary>
/// Conversation-level metadata carried by every outbound <see cref="ChannelMessage"/>.
/// Holds dimensions that characterise the conversation itself, not the message content
/// (remaining budget today; active agent, health, … as the framework grows).
/// </summary>
/// <param name="DustLevel">REMAINING dust as a fraction of the budget, fuel-gauge
/// semantics: 1.0 = full (nothing consumed), 0.0 = empty (budget spent or overshot).
/// Producers floor to whole-percent steps so gauges (<c>(int)(level * 100)</c>) and
/// the <c>DustLevel &lt;= 0.0</c> exhaustion gate agree: a fractional remainder under
/// 1% reads 0% on both sides. Null when dust limiting is disabled (frontends hide the
/// indicator in that case).</param>
public record ConversationMetadata(double? DustLevel = null);