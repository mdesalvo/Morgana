namespace Morgana.Contracts;

/// <summary>Pairs the <see cref="ChannelCoordinates"/> of a channel with the <see cref="ChannelCapabilities"/> budget it declared.</summary>
public record ChannelMetadata
{
    /// <summary>Identity and addressing coordinates of the channel.</summary>
    public required ChannelCoordinates Coordinates { get; init; }

    /// <summary>Expressive capability budget that the channel advertises.</summary>
    public required ChannelCapabilities Capabilities { get; init; }
}
