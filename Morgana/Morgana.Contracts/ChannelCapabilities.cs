namespace Morgana.Contracts;

/// <summary>
/// Declares the expressive capabilities of a channel (the transport + client pair
/// that ultimately renders Morgana's responses to an end user). Producers of
/// outbound messages (presenter, agents, supervisor) consult this contract to
/// decide whether a feature can be used as-is or must be degraded for the
/// target channel.
/// </summary>
/// <param name="SupportsRichCards">True if the channel can render <see cref="RichCard"/> payloads with structured components.</param>
/// <param name="SupportsQuickReplies">True if the channel can render <see cref="QuickReply"/> buttons.</param>
/// <param name="SupportsStreaming">True if the channel can deliver progressive chunks (e.g. SignalR ReceiveStreamChunk).</param>
/// <param name="SupportsMarkdown">True if the channel renders Markdown formatting in message text.</param>
/// <param name="MaxMessageLength">Optional hard limit on message text length in characters; null means no limit.</param>
public record ChannelCapabilities(
    bool SupportsRichCards,
    bool SupportsQuickReplies,
    bool SupportsStreaming,
    bool SupportsMarkdown,
    int? MaxMessageLength = null)
{
    /// <summary>
    /// Shared singleton representing the full legacy capability set (all features enabled,
    /// no length cap). Use this anywhere a "rich" channel needs to be described instead of
    /// allocating a new instance, both for the reference channel's static budget and for
    /// fallback paths (e.g. legacy conversations restored without a persisted handshake).
    /// </summary>
    public static readonly ChannelCapabilities Default = new ChannelCapabilities(
        SupportsRichCards: true,
        SupportsQuickReplies: true,
        SupportsStreaming: true,
        SupportsMarkdown: true,
        MaxMessageLength: null);
}
