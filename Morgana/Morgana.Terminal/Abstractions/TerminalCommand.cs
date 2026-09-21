using Morgana.Contracts;
using Morgana.Terminal.Interfaces;

namespace Morgana.Terminal.Abstractions;

/// <summary>
/// A command the palette offers. Every public concrete subclass, in this library or in the channel, is
/// discovered by <see cref="Services.TerminalCommandRegistryService"/> at startup and built from DI; the
/// registry runs it when the user asks. An exception it throws is reported by the UI as a failed command.
/// </summary>
public abstract class TerminalCommand
{
    /// <summary>How the palette lists the command. Its name and aliases must be unique among the palette's commands.</summary>
    public abstract CommandDescriptor Descriptor { get; }

    /// <summary>Whether it may run once the dust budget is exhausted, when nothing more can be asked of Morgana.</summary>
    public virtual bool AvailableWhenSpent => false;

    /// <summary>Runs the command on <paramref name="ui"/>; <paramref name="cancellationToken"/> fires when the user leaves or the process stops.</summary>
    public abstract Task ExecuteAsync(ITerminalUi ui, CancellationToken cancellationToken);
}