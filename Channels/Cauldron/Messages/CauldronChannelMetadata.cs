using Morgana.Contracts;

namespace Cauldron.Messages;

/// <summary>
/// Cauldron's channel handshake: <c>cauldron</c> delivered over <c>signalr</c>, with every rich feature on and no length cap.
/// Identity stays on the channel's side rather than on the shared ChannelMetadata contract, which describes the shape and not who fills it.
/// </summary>
public static class CauldronChannelMetadata
{
    /// <summary>The handshake that Cauldron presents to Morgana when it starts a conversation.</summary>
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