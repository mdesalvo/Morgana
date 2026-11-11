using Morgana.Contracts;
using Morgana.Terminal.Abstractions;
using Morgana.Terminal.Interfaces;

namespace Morgana.Terminal.Commands;

/// <summary><c>/exit</c>: leaves the channel, as Esc does outside the palette.</summary>
public sealed class TerminalExitCommand : TerminalCommand
{
    /// <summary>Builds the command for <paramref name="profile"/>'s channel.</summary>
    public TerminalExitCommand(ChannelProfile profile)
    {
        // The channel is named in the description, so the palette says what is being left.
        Descriptor = new CommandDescriptor("exit", $"Leave {profile.DisplayName}");
    }

    /// <inheritdoc />
    public override CommandDescriptor Descriptor { get; }

    /// <summary>True, because leaving is one of the two things still possible on a spent conversation.</summary>
    public override bool AvailableWhenSpent => true;

    /// <inheritdoc />
    public override Task ExecuteAsync(ITerminalUi ui, IReadOnlyDictionary<string, string> options, CancellationToken cancellationToken)
    {
        // The live UI returns and the lifecycle ends the conversation on screen.
        ui.RequestExit();
        return Task.CompletedTask;
    }
}