using Morgana.Contracts;

namespace Morgana.AI.Interfaces;

/// <summary>
/// A command Morgana executes on a conversation's behalf when a channel asks for it by name, outside
/// the turn pipeline: no guard, no classifier, no agent. Every implementation registered in DI is
/// published to the channels through <see cref="ICommandRegistryService"/>.
/// Reliability contract: the outcome must reach the user as at least one <see cref="ChannelMessage"/>
/// through <see cref="IChannelService"/>, since a channel holds its prompt until something lands.
/// </summary>
/// <remarks>
/// Nothing a command says is kept: a transcript is what was said in the conversation, while a command is
/// not a turn in it. Its outcome is shown on the channel that asked and leaves no trace, so a conversation read
/// back later runs from one turn to the next as though no command had been run between them. Whatever a
/// command writes on the record is therefore the work itself, never a report of it: <c>/compact</c> leaves a
/// summary in an agent's history because that is how a fold is carried, not to tell anyone it happened.
/// </remarks>
public interface ICommand
{
    /// <summary>
    /// How the command is published: its name, aliases and the line a palette shows. The name and
    /// every alias must be unique across the installed commands: a clash fails startup.
    /// </summary>
    CommandDescriptor Descriptor { get; }

    /// <summary>Runs the command on a conversation already known to exist, whose caller has passed the rate and dust limits, with the <paramref name="options"/> the user wrote at the prompt, already checked against what the descriptor declares.</summary>
    Task ExecuteAsync(string conversationId, IReadOnlyDictionary<string, string> options);
}