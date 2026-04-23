namespace Rune.Messages.Contracts;

/// <summary>
/// <para>
/// Body payload Rune sends to <c>POST /api/morgana/conversation/start</c>. Declares the
/// client-generated conversation id and the channel handshake in one shot, matching the
/// <c>Morgana.AI.Records.StartConversationRequest</c> shape consumed by the Morgana
/// controller gate.
/// </para>
/// <para>
/// CONTRACT VERSION: 1.0<br/>
/// SYNC WITH: Morgana.AI (Records.StartConversationRequest)
/// </para>
/// <para>
/// ⚠️ IMPORTANT: This DTO is duplicated in Morgana (Morgana.AI.Records.StartConversationRequest).
/// When making changes, update both versions in lockstep — there is no shared contracts project.
/// </para>
/// </summary>
public sealed class StartConversationRequest
{
    /// <summary>Stable id minted client-side so Morgana echoes it back on the 202 response. Required.</summary>
    public required string ConversationId { get; set; }

    /// <summary>Channel self-declaration (coordinates + capabilities). Required.</summary>
    public required ChannelMetadata ChannelMetadata { get; set; }
}
