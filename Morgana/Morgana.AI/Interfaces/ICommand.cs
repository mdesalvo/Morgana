using Morgana.Contracts;

namespace Morgana.AI.Interfaces;

/// <summary>
/// A command Morgana executes on a conversation's behalf when a channel asks for it by name, outside
/// the turn pipeline: no guard, no classifier, no agent. Every implementation registered in DI is
/// published to the channels through <see cref="ICommandRegistryService"/>.
/// Reliability contract: the outcome must reach the user through <see cref="IChannelService"/> as the
/// command's finished frame, a <see cref="ChannelMessage"/> whose <see cref="CommandProgress.Finished"/>
/// is set and whose text is the outcome. Every frame carries the invocation id the channel sent, which is
/// how a channel matches it to the run it is waiting on.
/// </summary>
public interface ICommand
{
    /// <summary>
    /// How the command is published: its name and the line a palette shows. The name must be unique
    /// across the installed commands: a clash fails startup.
    /// </summary>
    CommandDescriptor Descriptor { get; }

    /// <summary>Runs the command on a conversation already known to exist, whose caller has passed the rate and dust limits.</summary>
    /// <param name="conversationId">The conversation the command acts on, which is also where its frames are delivered.</param>
    /// <param name="invocationId">The channel's name for this run, to be set on every <see cref="CommandProgress"/> the command sends; null when the channel sent none.</param>
    /// <param name="options">The values the user wrote at the prompt, already checked against what the descriptor declares.</param>
    /// <param name="cancellationToken">
    /// Fires when the channel has stopped waiting, having already told the user the command was called off.
    /// A command cancelled before it writes leaves the record as it was and sends nothing to the channel: a
    /// late outcome would contradict what the user was shown. One whose write had already begun completes it
    /// and reports it, since that outcome is then the truth.
    /// </param>
    Task ExecuteAsync(string conversationId, string? invocationId, IReadOnlyDictionary<string, string> options, CancellationToken cancellationToken);
}