namespace Cauldron.Messages.Contracts;

/// <summary>
/// <para>
/// Identity + addressing coordinates declared by Cauldron at the conversation-start handshake:
/// who the channel is (<see cref="ChannelName"/>) and how Morgana should route outbound traffic
/// to it (<see cref="DeliveryMode"/>). Mirrors <c>Morgana.AI.Records.ChannelCoordinates</c>.
/// </para>
/// <para>
/// CONTRACT VERSION: 1.0<br/>
/// SYNC WITH: Morgana.AI (Records.ChannelCoordinates)
/// </para>
/// <para>
/// ⚠️ IMPORTANT: This DTO is duplicated in Morgana (Morgana.AI.Records.ChannelCoordinates).
/// When making changes, update both versions in lockstep — there is no shared contracts project.
/// </para>
/// </summary>
public sealed class ChannelCoordinates
{
    /// <summary>Stable identifier of the originating channel. Required.</summary>
    public required string ChannelName { get; set; }

    /// <summary>Transport dispatch key declared by the channel (e.g. <c>"signalr"</c>,
    /// <c>"webhook"</c>). Required — Morgana's start-conversation gate rejects handshakes
    /// whose value is missing or whitespace-only, and its channel service factory rejects
    /// keys it does not recognise at dispatch.</summary>
    public required string DeliveryMode { get; set; }
}
