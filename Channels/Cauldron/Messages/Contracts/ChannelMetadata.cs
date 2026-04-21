namespace Cauldron.Messages.Contracts;

/// <summary>
/// <para>
/// Channel identity descriptor declared by Cauldron at the conversation-start handshake.
/// Wraps the channel's <see cref="ChannelCoordinates"/> together with its
/// <see cref="ChannelCapabilities"/>, mirroring the <c>Morgana.AI.Records.ChannelMetadata</c>
/// schema consumed by Morgana's <c>AdaptingChannelService</c> to identify the originating
/// channel and decide how to degrade outbound messages.
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
    /// <summary>Identity + addressing coordinates of the channel (name, delivery mode, …). Required.</summary>
    public required ChannelCoordinates Coordinates { get; set; }

    /// <summary>Capability budget advertised by the originating channel. Required.</summary>
    public required ChannelCapabilities Capabilities { get; set; }

    /// <summary>
    /// Shared singleton describing Cauldron's metadata: channel name <c>"cauldron"</c>,
    /// delivery mode <c>"signalr"</c> and the full capability set. Reused by the conversation
    /// lifecycle service at the start handshake to avoid allocating a fresh instance per call.
    /// </summary>
    public static readonly ChannelMetadata Cauldron = new ChannelMetadata
    {
        Coordinates = new ChannelCoordinates
        {
            ChannelName = "cauldron",
            DeliveryMode = "signalr"
        },
        Capabilities = new ChannelCapabilities
        {
            SupportsRichCards = true,
            SupportsQuickReplies = true,
            SupportsStreaming = true,
            SupportsMarkdown = true,
            MaxMessageLength = null
        }
    };
}
