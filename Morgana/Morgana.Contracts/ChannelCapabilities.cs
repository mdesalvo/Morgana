namespace Morgana.Contracts;

/// <summary>Declares what a channel (the transport and client pair that renders Morgana's responses to an end user) can express.</summary>
/// <param name="SupportsRichCards">True when the channel renders a <see cref="RichCard"/>.</param>
/// <param name="SupportsQuickReplies">True when the channel renders <see cref="QuickReply"/> buttons.</param>
/// <param name="SupportsStreaming">True when the channel draws an answer as it is streamed.</param>
/// <param name="SupportsMarkdown">True when the channel renders markdown in the message text.</param>
/// <param name="MaxMessageLength">Hard limit on message text length in characters; null means no limit.</param>
// Producers of outbound messages (presenter, agents, supervisor) consult this contract to decide whether a feature
// can be used as-is or must be degraded for the target channel.
public record ChannelCapabilities(
    bool SupportsRichCards,
    bool SupportsQuickReplies,
    bool SupportsStreaming,
    bool SupportsMarkdown,
    int? MaxMessageLength = null);
