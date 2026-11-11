using Morgana.Contracts;

namespace Morgana.AI.Interfaces;

/// <summary>A command that Morgana runs on a conversation when a channel asks for it by name, outside the turn pipeline.</summary>
// No guard, classifier or agent takes part. Every implementation registered in DI is published through ICommandRegistryService.
// The outcome reaches the user through IChannelService as the command's finished frame: a ChannelMessage whose
// CommandProgress.Finished is set and whose text is the outcome. Every frame carries the invocation id that the channel sent,
// which is how a channel matches it to the run it is waiting on.
public interface ICommand
{
    /// <summary>The name and palette line under which the command is published, its name unique among the installed commands.</summary>
    CommandDescriptor Descriptor { get; }

    /// <summary>
    /// Runs the command on a conversation known to exist, whose caller has passed the rate and dust limits.
    /// A command cancelled before it writes leaves the record as it was and sends nothing, since a late outcome would
    /// contradict what the user was shown. One whose write had already begun completes it and reports it, because that
    /// outcome is then the truth.
    /// </summary>
    /// <param name="conversationId">The conversation the command acts on, which is also where its frames are delivered.</param>
    /// <param name="invocationId">The channel's name for this run, set on every <see cref="CommandProgress"/> the command sends; null when the channel sent none.</param>
    /// <param name="options">The values the user wrote at the prompt, already checked against what the descriptor declares.</param>
    /// <param name="cancellationToken">Fires when the channel has stopped waiting, having already told the user the command was called off.</param>
    Task ExecuteAsync(string conversationId, string? invocationId, IReadOnlyDictionary<string, string> options, CancellationToken cancellationToken);
}