using Morgana.Contracts;

namespace Cauldron.Messages;

/// <summary>
/// Cauldron's channel handshake (<c>channelName=cauldron</c>, <c>deliveryMode=signalr</c>,
/// the full rich-Web capability profile with every feature on and no length cap) over the
/// shared <see cref="ChannelMetadata"/> wire contract.
/// </summary>
public static class CauldronChannelMetadata
{
    public static readonly ChannelMetadata Profile = new()
    {
        Coordinates = new ChannelCoordinates
        {
            ChannelName = "cauldron",
            DeliveryMode = "signalr"
        },
        Capabilities = new ChannelCapabilities(
            SupportsRichCards: true,
            SupportsQuickReplies: true,
            SupportsStreaming: true,
            SupportsMarkdown: true,
            MaxMessageLength: null)
    };
}
