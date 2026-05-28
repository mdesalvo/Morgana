using Morgana.Contracts;

namespace Cauldron.Messages;

/// <summary>
/// Cauldron's channel handshake (<c>channelName=cauldron</c>, <c>deliveryMode=signalr</c>,
/// the full rich-Web capability profile with every feature on and no length cap) over the
/// shared <see cref="ChannelMetadata"/> wire contract.
/// </summary>
/// <remarks>
/// Lives channel-side (not on the contract) because channel identity and the declared
/// capability budget are Cauldron's concern, not part of the shared wire shape.
/// </remarks>
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
