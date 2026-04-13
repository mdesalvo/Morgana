namespace Cauldron.Messages.Contracts;

/// <summary>
/// <para>
/// Channel identity descriptor declared by Cauldron at the conversation-start handshake.
/// Wraps the channel name together with its <see cref="ChannelCapabilities"/>, mirroring
/// the <c>Morgana.AI.Records.ChannelMetadata</c> schema consumed by Morgana's
/// <c>AdaptingChannelService</c> to identify the originating channel and decide how to
/// degrade outbound messages.
/// </para>
/// <para>
/// CONTRACT VERSION: 1.0<br/>
/// SYNC WITH: Morgana.AI (Records.ChannelMetadata)
/// </para>
/// <para>
/// ⚠️ IMPORTANT: This DTO is duplicated in Morgana (Morgana.AI.Records.ChannelMetadata).
/// When making changes, update both versions in lockstep — there is no shared contracts project.
/// </para>
/// </summary>
public sealed class ChannelMetadata
{
    /// <summary>Stable identifier of the originating channel (e.g. <c>"cauldron"</c>, <c>"twilio-sms"</c>).</summary>
    public string ChannelName { get; set; } = string.Empty;

    /// <summary>Capability budget advertised by the originating channel.</summary>
    public ChannelCapabilities Capabilities { get; set; } = new ChannelCapabilities();

    /// <summary>
    /// Shared singleton describing Cauldron's metadata: channel name <c>"cauldron"</c> plus
    /// the full capability set. Reused by the conversation lifecycle service at the start
    /// handshake to avoid allocating a fresh instance per call.
    /// </summary>
    public static readonly ChannelMetadata Default = new ChannelMetadata
    {
        ChannelName = "cauldron",
        Capabilities = ChannelCapabilities.Default
    };
}