namespace Morgana.Contracts;

/// <summary>
/// Wraps a <see cref="ChannelCapabilities"/> budget with the <see cref="ChannelCoordinates"/>
/// of the channel that declared it, so Morgana can track "who the channel is and how to reach
/// it" (Cauldron web UI, Grimoire/Rune TUI, an IVR gateway, …) in addition to "what the
/// channel can render".
/// </summary>
public record ChannelMetadata
{
    /// <summary>Identity + addressing coordinates of the channel (name, delivery mode, …).</summary>
    public required ChannelCoordinates Coordinates { get; init; }

    /// <summary>Expressive capability budget advertised by the channel.</summary>
    public required ChannelCapabilities Capabilities { get; init; }
}