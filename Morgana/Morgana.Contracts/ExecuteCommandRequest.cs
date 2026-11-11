namespace Morgana.Contracts;

/// <summary>HTTP body for running one of Morgana's published commands on a conversation (<c>POST /api/morgana/conversation/{id}/command</c>), where the route names the conversation and the body only the command.</summary>
/// <param name="Name">The command as <see cref="CommandDescriptor.Name"/> names it, without slash.</param>
/// <param name="Confirmed">The channel's statement that the user answered Yes to the question a <see cref="CommandDescriptor.RequiresConfirmation"/> command asks.</param>
/// <param name="Options">The values the user wrote at the prompt, keyed by option name.</param>
/// <param name="InvocationId">The channel's own name for this one run, handed back on every <see cref="CommandProgress"/> it produces.</param>
// The outcome is delivered over the channel's own transport as every reply is. Confirmed stays false for every other command,
// which is why the gate is fail-closed: a channel that never implemented the question cannot run a confirming one.
// Options are refused when they are not what the command declares. InvocationId tells two runs of the same command apart,
// so a late outcome of an earlier one is never taken for the outcome of the run the user is waiting on.
// A null InvocationId leaves the channel matching by name alone.
public record ExecuteCommandRequest(
    string Name,
    bool Confirmed = false,
    IReadOnlyDictionary<string, string>? Options = null,
    string? InvocationId = null
);
