using Morgana.Contracts;

namespace Rune.Messages;

/// <summary>
/// Builds Rune's channel handshake (<c>channelName=rune</c>, <c>deliveryMode=webhook</c>,
/// the "poor but honest" capability profile with all rich features off) over the shared
/// <see cref="ChannelMetadata"/> wire contract.
/// </summary>
/// <remarks>
/// The factory lives channel-side (not on the contract) because channel identity and the
/// declared capability budget are Rune's concern, not part of the shared wire shape.
/// </remarks>
public static class RuneChannelMetadata
{
    public static ChannelMetadata Build(string callbackUrl, int? maxMessageLength) => new()
    {
        Coordinates = new ChannelCoordinates
        {
            ChannelName = "rune",
            DeliveryMode = "webhook",
            CallbackUrl = callbackUrl
        },
        Capabilities = new ChannelCapabilities(
            SupportsRichCards: false,
            SupportsQuickReplies: false,
            SupportsStreaming: false,
            SupportsMarkdown: false,
            MaxMessageLength: maxMessageLength)
    };
}
