namespace Rune.Messages.Contracts;

/// <summary>
/// <para>
/// Channel identity descriptor declared by Rune at the conversation-start handshake.
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
    /// <summary>Identity + addressing coordinates of the channel (name, delivery mode, callback URL). Required.</summary>
    public required ChannelCoordinates Coordinates { get; set; }

    /// <summary>Capability budget advertised by the originating channel. Required.</summary>
    public required ChannelCapabilities Capabilities { get; set; }

    /// <summary>
    /// Builds Rune's self-declaration for the handshake: channel name <c>"rune"</c>,
    /// delivery mode <c>"webhook"</c>, callback URL injected from configuration and the
    /// "povero ma non canaglia" capability profile (all expressive features off,
    /// <c>MaxMessageLength</c> tight enough to force <c>MorganaChannelAdapter.AdaptAsync</c>
    /// to rewrite anything beyond a short sentence).
    /// </summary>
    public static ChannelMetadata Build(string callbackUrl) => new ChannelMetadata
    {
        Coordinates = new ChannelCoordinates
        {
            ChannelName = "rune",
            DeliveryMode = "webhook",
            CallbackUrl = callbackUrl
        },
        Capabilities = new ChannelCapabilities
        {
            SupportsRichCards = false,
            SupportsQuickReplies = false,
            SupportsStreaming = false,
            SupportsMarkdown = false,
            MaxMessageLength = 200
        }
    };
}
