using Morgana.Contracts;

namespace Morgana.Terminal;

/// <summary>
/// Who a TTY channel is and what it claims it can render. Every shared service reads its identity
/// and its capability budget from here, so a channel states both once and the handshake, the JWT it
/// signs and the configuration section it reads can never drift apart.
/// </summary>
/// <param name="ChannelName">The channel as Morgana knows it: the <c>channelName</c> of the handshake and the <c>iss</c> of the token, lowercase.</param>
/// <param name="DisplayName">The channel as the terminal and the configuration file spell it, also the root of its settings section.</param>
/// <param name="Capabilities">The expressive surface the channel declares; Morgana's adapter degrades every reply to fit it.</param>
public sealed record ChannelProfile(string ChannelName, string DisplayName, ChannelCapabilities Capabilities)
{
    /// <summary>Reads a setting of this channel, addressing it under the channel's own root.</summary>
    public string SectionKey(string key) => $"{DisplayName}:{key}";

    /// <summary>
    /// Builds the handshake announced when the conversation opens. Identity and capability budget
    /// stay channel-side rather than on the wire contract, which describes the shape and not who fills it.
    /// </summary>
    public ChannelMetadata BuildMetadata(string callbackUrl) => new()
    {
        Coordinates = new ChannelCoordinates
        {
            ChannelName = ChannelName,
            DeliveryMode = "webhook",
            CallbackUrl = callbackUrl
        },
        Capabilities = Capabilities
    };
}
