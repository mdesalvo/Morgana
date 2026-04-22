namespace Rune.Messages.Contracts;

/// <summary>
/// Quick reply button for user interaction in chat messages.
/// Rune declares <c>SupportsQuickReplies=false</c> so this DTO should be stripped by
/// Morgana's <c>MorganaChannelAdapter</c> before delivery — kept here only for binary
/// compatibility with the shared wire contract.
/// </summary>
public class QuickReply
{
    public string Id { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public bool Termination { get; set; }
}
