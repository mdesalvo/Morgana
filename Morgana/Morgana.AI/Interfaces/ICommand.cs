using Morgana.Contracts;

namespace Morgana.AI.Interfaces;

/// <summary>
/// A command Morgana executes on a conversation's behalf when a channel asks for it by name, outside
/// the turn pipeline: no guard, no classifier, no agent. Every implementation registered in DI is
/// published to the channels through <see cref="ICommandRegistryService"/>.
/// Reliability contract: the outcome must reach the user through <see cref="IChannelService"/> as the
/// command's finished frame, a <see cref="ChannelMessage"/> whose <see cref="CommandProgress.Finished"/>
/// is set and whose text is the outcome: a channel matches it to the command it is waiting on by name.
/// </summary>
public interface ICommand
{
    /// <summary>
    /// How the command is published: its name and the line a palette shows. The name must be unique
    /// across the installed commands: a clash fails startup.
    /// </summary>
    CommandDescriptor Descriptor { get; }

    /// <summary>Runs the command on a conversation already known to exist, whose caller has passed the rate and dust limits, with the <paramref name="options"/> the user wrote at the prompt, already checked against what the descriptor declares.</summary>
    /// <param name="cancellationToken">
    /// Fires when the channel has stopped waiting, having already told the user the command was called off.
    /// A command cancelled before it writes leaves the record as it was and sends nothing to the channel: a
    /// late outcome would contradict what the user was shown. One whose write had already begun completes it
    /// and reports it, since that outcome is then the truth.
    /// </param>
    Task ExecuteAsync(string conversationId, IReadOnlyDictionary<string, string> options, CancellationToken cancellationToken);
}