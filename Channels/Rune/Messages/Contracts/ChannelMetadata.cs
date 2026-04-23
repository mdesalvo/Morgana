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
    /// to rewrite anything beyond a short sentence). The budget is parameterised so users
    /// who want a less aggressive downgrade can raise it via <c>Rune:MaxMessageLength</c>
    /// in <c>appsettings.json</c> without touching the contract.
    /// </summary>
    /// <param name="callbackUrl">Absolute URL Morgana will POST outbound messages to
    /// (from <c>Rune:CallbackURL</c>).</param>
    /// <param name="maxMessageLength">Hard cap on the rewritten message text advertised
    /// to Morgana's channel adapter. <c>null</c> means "no cap"; any positive int is
    /// enforced by the adapter's length-budget stage.</param>
    public static ChannelMetadata Build(string callbackUrl, int? maxMessageLength) => new ChannelMetadata
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
            MaxMessageLength = maxMessageLength
        }
    };
}
