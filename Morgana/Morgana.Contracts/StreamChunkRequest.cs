namespace Morgana.Contracts;

/// <summary>
/// Body payload Morgana POSTs to <c>{callbackUrl}/chunk</c> for each streamed agent token,
/// mirroring SignalR's <c>ReceiveStreamChunk</c> contract: a minimal conversation-id + chunk-text
/// pair, no metadata. The chunk is an incremental delta (not cumulative) — concatenate the
/// chunks in arrival order to recover the partial assistant response.
/// </summary>
/// <param name="ConversationId">Target conversation id (echoed from the active conversation).</param>
/// <param name="ChunkText">Incremental chunk text (delta from the agent's streaming response).</param>
public record StreamChunkRequest(string ConversationId, string ChunkText);
