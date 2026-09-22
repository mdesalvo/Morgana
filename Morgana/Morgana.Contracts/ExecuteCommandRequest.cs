namespace Morgana.Contracts;

/// <summary>
/// HTTP request model for running one of Morgana's published commands on a conversation
/// (<c>POST /api/morgana/conversation/{id}/command</c>). Its outcome is delivered over the channel's
/// own transport, as every reply is.
/// </summary>
/// <param name="ConversationId">Unique identifier of the target conversation</param>
/// <param name="Name">The command as <see cref="CommandDescriptor.Name"/> or one of its aliases names it, without slash</param>
/// <param name="Confirmed">
/// The channel's statement that the user answered Yes to the question a
/// <see cref="CommandDescriptor.RequiresConfirmation"/> command asks. Left false for every other command,
/// which is why the gate is fail-closed: a channel that never implemented the question cannot run one
/// </param>
/// <param name="Options">The values the user wrote at the prompt, keyed by option name; refused when they are not what the command declares</param>
public record ExecuteCommandRequest(
    string ConversationId,
    string Name,
    bool Confirmed = false,
    IReadOnlyDictionary<string, string>? Options = null
);