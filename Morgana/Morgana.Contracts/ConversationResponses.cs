namespace Morgana.Contracts;

/// <summary>
/// Response body of <c>POST /api/morgana/conversation/start</c> (202 Accepted).
/// </summary>
/// <remarks>
/// Conversation creation is queued on the actor system, so the response only acknowledges the
/// request: the conversation id is echoed back and is authoritative from that point on, while
/// the presentation message arrives later over the channel's own transport.
/// </remarks>
/// <param name="ConversationId">Conversation id, echoed from the request.</param>
/// <param name="Message">Informational status line; carries no control semantics.</param>
public record StartConversationResponse(
    string ConversationId,
    string Message);

/// <summary>
/// Response body of <c>POST /api/morgana/conversation/{id}/resume</c> (202 Accepted).
/// </summary>
/// <param name="ConversationId">Conversation id being resumed.</param>
/// <param name="Resumed">Always true on this path; present so the shape reads unambiguously.</param>
/// <param name="ActiveAgent">Most recent active agent, or null when the conversation was last
/// held by base Morgana — nullable because the persistence lookup legitimately finds nothing.</param>
/// <param name="DustLevel">Remaining dust as a fraction of the conversation budget (fuel-gauge
/// semantics: 1.0 = full, 0.0 = empty), floored to whole-percent steps. Null when dust limiting
/// is disabled, in which case the client hides its gauge.</param>
/// <param name="DustExhaustedMessage">Canonical terminal lockout message when the resumed
/// conversation is already dust-dead, so the client can re-surface the banner up front instead
/// of letting the user rediscover the wall by firing a message. Null otherwise.</param>
public record ResumeConversationResponse(
    string ConversationId,
    bool Resumed,
    string? ActiveAgent,
    double? DustLevel,
    string? DustExhaustedMessage);

/// <summary>
/// Response body of <c>GET /api/morgana/conversation/{id}/history</c> (200 OK).
/// </summary>
/// <param name="Messages">Conversation messages in chronological order across all participating
/// agents. Never empty on a 200: an empty history is reported as 404.</param>
public record ConversationHistoryResponse(
    MorganaChatMessage[] Messages);
