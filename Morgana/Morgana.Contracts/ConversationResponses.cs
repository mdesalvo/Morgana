namespace Morgana.Contracts;

/// <summary>Response body of <c>POST /api/morgana/conversation/start</c> (202 Accepted).</summary>
/// <remarks>Conversation creation is queued on the actor system, so the response only acknowledges the request.</remarks>
/// <param name="ConversationId">The conversation id, echoed from the request.</param>
/// <param name="Message">Informational status line that carries no control semantics.</param>
// The echoed ConversationId is authoritative from that point on while the presentation message arrives later over the channel's own transport.
public record StartConversationResponse(
    string ConversationId,
    string Message);

/// <summary>Response body of <c>POST /api/morgana/conversation/{id}/resume</c> (202 Accepted).</summary>
/// <param name="ConversationId">The conversation being resumed.</param>
/// <param name="Resumed">Always true on this path.</param>
/// <param name="ActiveAgent">Most recent active agent; null when the conversation was last held by base Morgana.</param>
/// <param name="ConversationMetadata">The conversation as every pushed message describes it: the dust level and the channel's reply wait.</param>
/// <param name="DustExhaustedMessage">Canonical terminal lockout message when the resumed conversation is already dust-dead; null otherwise.</param>
// DustExhaustedMessage lets the client re-surface the banner up front instead of letting the user rediscover the wall by firing a message.
public record ResumeConversationResponse(
    string ConversationId,
    bool Resumed,
    string? ActiveAgent,
    ConversationMetadata ConversationMetadata,
    string? DustExhaustedMessage);

/// <summary>Response body of <c>GET /api/morgana/conversation/{id}/history</c> (200 OK).</summary>
/// <param name="Messages">Conversation messages in chronological order across all participating agents.</param>
/// <param name="ConversationMetadata">The conversation as every pushed message describes it: the dust level and the channel's reply wait.</param>
// Messages is empty while nobody has spoken; a conversation that was never started is answered with 404 instead.
public record ConversationHistoryResponse(
    MorganaChatMessage[] Messages,
    ConversationMetadata ConversationMetadata);

/// <summary>Response body of <c>GET /api/morgana/commands</c> (200 OK) listing every command Morgana executes on a conversation's behalf.</summary>
/// <param name="Commands">The published commands in the order a palette should list them.</param>
// Commands is empty when none is installed and never null.
public record CommandCatalogResponse(IReadOnlyList<CommandDescriptor> Commands);
