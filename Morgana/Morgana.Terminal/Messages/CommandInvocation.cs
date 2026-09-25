using Morgana.Contracts;

namespace Morgana.Terminal.Messages;

/// <summary>
/// One command as the user asked for it: which command it is, with the values read off the line. It travels
/// from the prompt through the confirmation question to the run, so what is finally executed is what was
/// shown in the question, never the line read a second time.
/// </summary>
/// <param name="Command">The command the typed name resolved to.</param>
/// <param name="Options">The values written after the name, already checked against what the command declares.</param>
public sealed record CommandInvocation(
    CommandDescriptor Command,
    IReadOnlyDictionary<string, string> Options)
{
    /// <summary>
    /// This run's own name, sent to Morgana with the request and handed back on every frame of it: the UI
    /// takes an outcome only from the run it is waiting on, never from an earlier one of the same command.
    /// </summary>
    public string Id { get; } = Guid.NewGuid().ToString("N");
}
