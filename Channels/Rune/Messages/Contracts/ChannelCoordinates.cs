namespace Rune.Messages.Contracts;

/// <summary>
/// <para>
/// Identity + addressing coordinates declared by Rune at the conversation-start handshake:
/// who the channel is (<see cref="ChannelName"/>), how Morgana should route outbound traffic
/// to it (<see cref="DeliveryMode"/>) and — for webhook mode — where to deliver it
/// (<see cref="CallbackUrl"/>). Mirrors <c>Morgana.AI.Records.ChannelCoordinates</c>.
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

    /// <summary>
    /// Absolute callback URL where Morgana should POST outbound messages when
    /// <see cref="DeliveryMode"/> is <c>"webhook"</c>. Nullable for transports that do not
    /// need a callback (e.g. <c>signalr</c>). Required and validated as an absolute URI at the
    /// Morgana controller gate when <see cref="DeliveryMode"/> equals <c>"webhook"</c>.
    /// </summary>
    public string? CallbackUrl { get; set; }
}
