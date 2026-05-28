namespace Morgana.Contracts;

/// <summary>
/// Wraps a <see cref="ChannelCapabilities"/> budget with the <see cref="ChannelCoordinates"/>
/// of the channel that declared it, so Morgana can track "who the channel is and how to reach
/// it" (Cauldron web UI, a Twilio SMS bridge, an IVR gateway, …) in addition to "what the
/// channel can render".
/// </summary>
/// <remarks>
/// <para>Identity/addressing and expressive budget are kept in two distinct sub-records so
/// each can evolve independently: new addressing fields (callback URLs, routing keys) land
/// on <see cref="ChannelCoordinates"/>, new rendering features land on
/// <see cref="ChannelCapabilities"/>.</para>
/// </remarks>
public record ChannelMetadata
{
    /// <summary>Identity + addressing coordinates of the channel (name, delivery mode, …).</summary>
    public required ChannelCoordinates Coordinates { get; init; }

    /// <summary>Expressive capability budget advertised by the channel.</summary>
    public required ChannelCapabilities Capabilities { get; init; }
}