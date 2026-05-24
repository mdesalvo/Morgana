namespace Grimoire.Messages.Contracts;

/// <summary>
/// Quick reply button for user interaction in chat messages.
/// Grimoire declares <c>SupportsQuickReplies=true</c>, so this DTO arrives integral (Morgana's
/// <c>MorganaChannelAdapter</c> does not strip it) and is surfaced as a blocking selection prompt.
/// </summary>
public class QuickReply
{
    public string Id { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public bool Termination { get; set; }
}
