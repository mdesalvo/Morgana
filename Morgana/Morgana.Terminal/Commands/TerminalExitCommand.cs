using Morgana.Contracts;
using Morgana.Terminal.Abstractions;
using Morgana.Terminal.Interfaces;

namespace Morgana.Terminal.Commands;

/// <summary><c>/exit</c>, also reached as <c>/esc</c>: leaves the channel, as Esc does outside the palette.</summary>
public sealed class TerminalExitCommand : TerminalCommand
{
    /// <summary>Names the channel in the description, so the palette says what is being left.</summary>
    public TerminalExitCommand(ChannelProfile profile)
    {
        Descriptor = new CommandDescriptor("exit", $"Leave {profile.DisplayName}", Aliases: ["esc"]);
    }

    /// <inheritdoc />
    public override CommandDescriptor Descriptor { get; }

    /// <summary>Leaving is one of the two things still possible on a spent conversation.</summary>
    public override bool AvailableWhenSpent => true;

    /// <inheritdoc />
    public override Task ExecuteAsync(ITerminalUi ui, IReadOnlyDictionary<string, string> options, CancellationToken cancellationToken)
    {
        // The live UI returns and the lifecycle ends the conversation on screen
        ui.RequestExit();
        return Task.CompletedTask;
    }
}