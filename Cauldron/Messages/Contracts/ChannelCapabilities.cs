namespace Cauldron.Messages.Contracts;

/// <summary>
/// <para>
/// Capability descriptor declared by Cauldron at the conversation-start handshake.
/// Mirrors the <c>Morgana.AI.Records.ChannelCapabilities</c> schema consumed by Morgana's
/// <c>AdaptingChannelService</c> to decide whether (and how) to degrade outbound messages.
/// </para>
/// <para>
/// CONTRACT VERSION: 1.0<br/>
/// SYNC WITH: Morgana.AI (Records.ChannelCapabilities)
/// </para>
/// <para>
/// ⚠️ IMPORTANT: This DTO is duplicated in Morgana (Morgana.AI.Records.ChannelCapabilities).
/// When making changes, update both versions in lockstep — there is no shared contracts project.
/// </para>
/// </summary>
public sealed class ChannelCapabilities
{
    /// <summary>True if Cauldron can render <see cref="RichCard"/> payloads. Required.</summary>
    public required bool SupportsRichCards { get; set; }

    /// <summary>True if Cauldron can render <see cref="QuickReply"/> buttons. Required.</summary>
    public required bool SupportsQuickReplies { get; set; }

    /// <summary>True if Cauldron can consume the SignalR <c>ReceiveStreamChunk</c> event. Required.</summary>
    public required bool SupportsStreaming { get; set; }

    /// <summary>True if Cauldron renders Markdown formatting in message text. Required.</summary>
    public required bool SupportsMarkdown { get; set; }

    /// <summary>Optional hard limit on message text length in characters; null means no limit.</summary>
    public int? MaxMessageLength { get; set; }
}
