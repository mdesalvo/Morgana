namespace Morgana.Contracts;

/// <summary>
/// Conversation-level metadata carried by every outbound <see cref="ChannelMessage"/>, describing the conversation rather than the message content.
/// Every chunk received starts the ReplyTimeoutSeconds wait again, so it bounds silence and not the whole turn.
/// </summary>
/// <param name="DustLevel">Remaining dust as a fraction of the budget, from 1.0 (nothing consumed) down to 0.0 (budget spent or overshot).</param>
/// <param name="ReplyTimeoutSeconds">Seconds the channel waits on a silent turn before giving it up, as Morgana computes it from her own deadlines.</param>
public record ConversationMetadata(double? DustLevel = null, int? ReplyTimeoutSeconds = null);