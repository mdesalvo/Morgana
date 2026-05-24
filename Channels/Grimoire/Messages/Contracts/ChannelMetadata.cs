namespace Grimoire.Messages.Contracts;

/// <summary>
/// <para>
/// Channel identity descriptor declared by Grimoire at the conversation-start handshake.
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
    /// Builds Grimoire's self-declaration for the handshake: channel name <c>"grimoire"</c>,
    /// delivery mode <c>"webhook"</c>, callback URL injected from configuration and the
    /// full TTY capability profile (rich cards, quick replies, streaming and markdown all
    /// supported, no message-length cap). Grimoire is the rich-TTY sibling of Cauldron:
    /// content from Morgana arrives integral and Grimoire renders it locally with Spectre
    /// instead of relying on <c>MorganaChannelAdapter.AdaptAsync</c> to degrade it — the
    /// adapter's short-circuit hot path is taken on every turn.
    /// </summary>
    /// <param name="callbackUrl">Absolute URL Morgana will POST outbound messages to
    /// (from <c>Grimoire:CallbackURL</c>).</param>
    public static ChannelMetadata Build(string callbackUrl) => new ChannelMetadata
    {
        Coordinates = new ChannelCoordinates
        {
            ChannelName = "grimoire",
            DeliveryMode = "webhook",
            CallbackUrl = callbackUrl
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
