namespace Grimoire.Messages.Contracts;

/// <summary>
/// <para>
/// Body payload Morgana POSTs to <c>{callbackUrl}/chunk</c> for each streamed agent token.
/// Mirrors SignalR's <c>ReceiveStreamChunk</c> contract: a minimal conversation-id + chunk-text
/// pair, no metadata. The chunk is an incremental delta (not cumulative) — concatenate them in
/// arrival order to recover the partial assistant response.
/// </para>
/// <para>
/// CONTRACT VERSION: 1.0<br/>
/// SYNC WITH: Morgana.Web (WebhookChannelService.StreamChunkRequest)
/// </para>
/// <para>
/// ⚠️ IMPORTANT: This DTO mirrors a wire shape defined in Morgana.Web. When making changes,
/// update both sides in lockstep — there is no shared contracts project.
/// </para>
/// </summary>
public sealed class StreamChunkRequest
{
    /// <summary>Target conversation id (echoed by Morgana from the active conversation).</summary>
    public string ConversationId { get; set; } = string.Empty;

    /// <summary>Incremental chunk text (delta from the agent's streaming response).</summary>
    public string ChunkText { get; set; } = string.Empty;
}
