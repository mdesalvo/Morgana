using Morgana.Contracts;
using Morgana.Terminal.Abstractions;
using Morgana.Terminal.Interfaces;
using Morgana.Terminal.Services;

namespace Morgana.Terminal.Commands;

/// <summary><c>/new</c>: replaces the conversation on screen with a fresh one, without restarting the process.</summary>
public sealed class TerminalNewCommand : TerminalCommand
{
    /// <summary>Opens the fresh conversation and makes it the one on screen.</summary>
    private readonly TerminalSessionService session;

    /// <summary>Ends the conversation left behind.</summary>
    private readonly MorganaClientService morganaClientService;

    /// <summary>Builds the command on the session and the REST client.</summary>
    public TerminalNewCommand(TerminalSessionService session, MorganaClientService morganaClientService)
    {
        // The session opens the fresh conversation and the client ends the old one.
        this.session = session;
        this.morganaClientService = morganaClientService;
    }

    /// <inheritdoc />
    // The conversation on screen is ended by this, so the user is asked to confirm before anything is lost.
    public override CommandDescriptor Descriptor { get; } =
        new("new", "Start a fresh conversation with Morgana", RequiresConfirmation: true);

    /// <summary>True, because a spent conversation is exactly what this command is the way out of.</summary>
    public override bool AvailableWhenSpent => true;

    /// <inheritdoc />
    public override Task ExecuteAsync(ITerminalUi ui, IReadOnlyDictionary<string, string> options, CancellationToken cancellationToken) =>
        // The screen swaps only once the fresh conversation exists; opening it is this command's own business.
        ui.ReplaceConversationAsync(OpenFreshConversationAsync, cancellationToken);

    /// <summary>Opens the fresh conversation, then ends the one it replaces, returning the new id.</summary>
    private async Task<string> OpenFreshConversationAsync(CancellationToken cancellationToken)
    {
        // The fresh conversation is opened before the old one is ended: a Morgana that refuses or cannot be
        // reached throws here and leaves the user in the conversation they were in.
        string previousConversationId = session.ConversationId;
        string openedConversationId = await session.OpenConversationAsync(cancellationToken);

        // The conversation left behind is closed on Morgana's side without the fresh one waiting on it,
        // since ending is best effort and never throws.
        _ = morganaClientService.EndConversationAsync(previousConversationId, cancellationToken);

        // The fresh conversation is the one the screen now shows.
        return openedConversationId;
    }
}