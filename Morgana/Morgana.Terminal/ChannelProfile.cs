using Morgana.Contracts;

namespace Morgana.Terminal;

/// <summary>
/// Who a TTY channel is and what it claims it can render, read by every shared service.
/// A channel states its identity and its capabilities once, so the handshake, the token it signs and the configuration
/// section it reads can never drift apart.
/// </summary>
/// <param name="ChannelName">The channel as Morgana knows it: the <c>channelName</c> of the handshake and the <c>iss</c> of the token, lowercase.</param>
/// <param name="DisplayName">The channel as the terminal and the configuration file spell it, also the root of its settings section.</param>
/// <param name="Capabilities">The expressive surface the channel declares; Morgana's adapter degrades every reply to fit it.</param>
/// <param name="Introduction">What <c>/help</c> tells a newcomer first: what the channel is and how talking through it goes, as prose.</param>
/// <param name="KeyBindings">The keys <c>/help</c> lists, each with what it does here, in the order a newcomer needs them.</param>
public sealed record ChannelProfile(
    string ChannelName,
    string DisplayName,
    ChannelCapabilities Capabilities,
    string Introduction,
    IReadOnlyList<(string Keys, string Action)> KeyBindings)
{
    /// <summary>The configuration key of the setting <paramref name="key"/> under the channel's own root.</summary>
    public string SectionKey(string key) => $"{DisplayName}:{key}";

    /// <summary>The handshake announced when the conversation opens, delivered by webhook to <paramref name="callbackUrl"/>.</summary>
    // Identity and capability budget stay on the channel's side rather than on the wire contract, which describes the shape and not who fills it.
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