namespace Cauldron.Messages;

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
    /// <summary>True if Cauldron can render <see cref="RichCard"/> payloads.</summary>
    public bool SupportsRichCards { get; set; }

    /// <summary>True if Cauldron can render <see cref="QuickReply"/> buttons.</summary>
    public bool SupportsQuickReplies { get; set; }

    /// <summary>True if Cauldron can consume the SignalR <c>ReceiveStreamChunk</c> event.</summary>
    public bool SupportsStreaming { get; set; }

    /// <summary>True if Cauldron renders Markdown formatting in message text.</summary>
    public bool SupportsMarkdown { get; set; }

    /// <summary>Optional hard limit on message text length in characters; null means no limit.</summary>
    public int? MaxMessageLength { get; set; }

    /// <summary>
    /// Shared singleton describing Cauldron's full capability set. Reused by the conversation
    /// lifecycle service at the start handshake to avoid allocating a fresh instance per call.
    /// </summary>
    public static readonly ChannelCapabilities Default = new ChannelCapabilities
    {
        SupportsRichCards = true,
        SupportsQuickReplies = true,
        SupportsStreaming = true,
        SupportsMarkdown = true,
        MaxMessageLength = null
    };
}
