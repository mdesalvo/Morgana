namespace Morgana.Contracts;

/// <summary>
/// Body that Morgana POSTs to <c>{callbackUrl}/chunk</c> for each streamed agent token, mirroring SignalR's <c>ReceiveStreamChunk</c> contract.
/// Each chunk is an incremental delta and not cumulative text: concatenating the chunks in arrival order recovers the partial assistant response.
/// </summary>
/// <param name="ConversationId">The conversation the streamed answer belongs to.</param>
/// <param name="ChunkText">The text that this chunk adds to the answer.</param>
public record StreamChunkRequest(
    string ConversationId,
    string ChunkText);