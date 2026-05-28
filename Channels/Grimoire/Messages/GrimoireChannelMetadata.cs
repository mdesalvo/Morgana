using Morgana.Contracts;

namespace Grimoire.Messages;

/// <summary>
/// Builds Grimoire's channel handshake (<c>channelName=grimoire</c>, <c>deliveryMode=webhook</c>,
/// the full rich-TTY capability profile with every feature on and no length cap) over the shared
/// <see cref="ChannelMetadata"/> wire contract.
/// </summary>
/// <remarks>
/// The factory lives channel-side (not on the contract) because channel identity and the
/// declared capability budget are Grimoire's concern, not part of the shared wire shape.
/// </remarks>
public static class GrimoireChannelMetadata
{
    public static ChannelMetadata Build(string callbackUrl) => new()
    {
        Coordinates = new ChannelCoordinates
        {
            ChannelName = "grimoire",
            DeliveryMode = "webhook",
            CallbackUrl = callbackUrl
        },
        Capabilities = new ChannelCapabilities(
            SupportsRichCards: true,
            SupportsQuickReplies: true,
            SupportsStreaming: true,
            SupportsMarkdown: true,
            MaxMessageLength: null)
    };
}
