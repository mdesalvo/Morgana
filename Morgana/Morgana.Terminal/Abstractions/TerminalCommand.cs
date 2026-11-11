using Morgana.Contracts;
using Morgana.Terminal.Interfaces;

namespace Morgana.Terminal.Abstractions;

/// <summary>A command the palette offers, run by the registry when the user asks for it.</summary>
// Every public concrete subclass, in this library or in the channel, is discovered by TerminalCommandRegistryService at
// startup and built from DI. An exception it throws is reported by the UI as a failed command.
public abstract class TerminalCommand
{
    /// <summary>How the palette lists the command, its name unique among the palette's commands.</summary>
    public abstract CommandDescriptor Descriptor { get; }

    /// <summary>Whether it may run once the dust budget is exhausted, when nothing more can be asked of Morgana.</summary>
    public virtual bool AvailableWhenSpent => false;

    /// <summary>Runs the command on <paramref name="ui"/> with the <paramref name="options"/> the user wrote, already checked against what the descriptor declares.</summary>
    // The cancellation token fires when the user leaves or the process stops.
    public abstract Task ExecuteAsync(ITerminalUi ui, IReadOnlyDictionary<string, string> options, CancellationToken cancellationToken);
}