namespace Morgana.Contracts;

/// <summary>
/// HTTP request model for running one of Morgana's published commands on a conversation
/// (<c>POST /api/morgana/conversation/{id}/command</c>). Its outcome is delivered over the channel's
/// own transport, as every reply is.
/// </summary>
/// <param name="ConversationId">Unique identifier of the target conversation</param>
/// <param name="Name">The command as <see cref="CommandDescriptor.Name"/> or one of its aliases names it, without slash</param>
public record ExecuteCommandRequest(
    string ConversationId,
    string Name
);