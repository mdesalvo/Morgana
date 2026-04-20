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
    /// <summary>Stable identifier of the originating channel. Required.</summary>
    public required string ChannelName { get; set; }

    /// <summary>Capability budget advertised by the originating channel. Required.</summary>
    public required ChannelCapabilities Capabilities { get; set; }

    /// <summary>Transport dispatch key declared by the channel (e.g. <c>"signalr"</c>,
    /// <c>"webhook"</c>). Required — Morgana's start-conversation gate rejects handshakes
    /// whose value is missing or whitespace-only, and its channel service factory rejects
    /// keys it does not recognise at dispatch.</summary>
    public required string DeliveryMode { get; set; }

    /// <summary>
    /// Shared singleton describing Cauldron's metadata: channel name <c>"cauldron"</c>,
    /// delivery mode <c>"signalr"</c> and the full capability set. Reused by the conversation
    /// lifecycle service at the start handshake to avoid allocating a fresh instance per call.
    /// </summary>
    public static readonly ChannelMetadata Cauldron = new ChannelMetadata
    {
        ChannelName = "cauldron",
        DeliveryMode = "signalr",
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