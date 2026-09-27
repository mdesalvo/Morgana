using System.Text;
using Morgana.Contracts;
using Morgana.Terminal.Abstractions;
using Morgana.Terminal.Interfaces;
using Morgana.Terminal.Messages;
using Morgana.Terminal.Services;

namespace Morgana.Terminal.Commands;

/// <summary>
/// <c>/resume id:… seal:…</c>: puts back on screen a conversation this channel opened earlier in this process
/// or in another one, then carries it on. Morgana admits it only with the seal handed over at its start and only
/// to the channel that started it, so a conversation begun elsewhere cannot be picked up here.
/// </summary>
public sealed class TerminalResumeCommand : TerminalCommand
{
    /// <summary>What the user reads when Morgana refuses: it never says whether the id or the seal was wrong.</summary>
    private const string RefusedOutcome = "no conversation with that ID and seal";

    /// <summary>Symbols in a seal as Morgana issues it: 80 bits at five per symbol.</summary>
    private const int SealSymbolCount = 16;

    /// <summary>Symbols between two dashes of a seal as Morgana issues it.</summary>
    private const int SealGroupLength = 4;

    /// <summary>Makes the resumed conversation the one on screen.</summary>
    private readonly TerminalSessionService session;

    /// <summary>Resumes the conversation, reads its record and ends the one left behind.</summary>
    private readonly MorganaClientService morganaClientService;

    /// <summary>Captures the session and the REST client.</summary>
    public TerminalResumeCommand(TerminalSessionService session, MorganaClientService morganaClientService)
    {
        this.session = session;
        this.morganaClientService = morganaClientService;
    }

    /// <inheritdoc />
    public override CommandDescriptor Descriptor { get; } = new(
        "resume",
        "Pick up a conversation this channel opened earlier",

        // The conversation on screen is ended by this, so it is asked for twice before anything is lost
        RequiresConfirmation: true,
        Options:
        [
            new CommandOption("id", "the conversation's id, as /status shows it", Required: true),
            new CommandOption("seal", "the seal /status showed beside that id", Required: true)
        ]);

    /// <summary>A spent conversation is one the user may well want to leave for an earlier one.</summary>
    public override bool AvailableWhenSpent => true;

    /// <inheritdoc />
    public override async Task ExecuteAsync(ITerminalUi ui, IReadOnlyDictionary<string, string> options, CancellationToken cancellationToken)
    {
        string conversationId = options["id"].Trim();
        string seal = options["seal"].Trim();

        // Resuming the conversation on screen would end it right after putting it back
        if (string.Equals(conversationId, session.ConversationId, StringComparison.Ordinal))
        {
            ui.ShowCommandOutcome("this conversation is already the one on screen");
            return;
        }

        // Asked before the screen is touched: a refusal leaves the user exactly where they were
        ui.ShowProgress(new CommandProgress(Descriptor.Name, "asking Morgana", Completed: 0, Total: 1));
        ResumeConversationResponse? resumed = await morganaClientService.ResumeConversationAsync(conversationId, seal, cancellationToken);
        if (resumed is null)
        {
            ui.ShowCommandOutcome(RefusedOutcome, isFailure: true);
            return;
        }

        // Morgana forgave how the seal was typed; the screen shows it as Morgana issued it
        await ui.ReplaceConversationAsync(token => TakeOverAsync(resumed, SpellAsIssued(seal), token), cancellationToken);
    }

    /// <summary>
    /// A seal Morgana has just admitted, spelled back as it was issued: four dash-separated groups of four in
    /// upper case, with the look-alikes Morgana folds when checking it folded the same way. Otherwise what
    /// <c>/status</c> hands out for the next resume would be one person's transcription of it.
    /// </summary>
    private static string SpellAsIssued(string seal)
    {
        StringBuilder symbols = new StringBuilder(seal.Length);
        foreach (char character in seal.ToUpperInvariant())
        {
            if (character == '-' || char.IsWhiteSpace(character))
                continue;
            symbols.Append(character switch
            {
                'O' => '0',
                'I' or 'L' => '1',
                _ => character
            });
        }

        // A seal of another length is not Morgana's format: it is kept as folded rather than regrouped by guess
        if (symbols.Length != SealSymbolCount)
            return symbols.ToString();
        return string.Join('-', Enumerable.Range(0, SealSymbolCount / SealGroupLength)
            .Select(group => symbols.ToString(group * SealGroupLength, SealGroupLength)));
    }

    /// <summary>Reads the resumed conversation's record, makes it the one on screen, then ends the one it replaces.</summary>
    private async Task<ConversationOnScreen> TakeOverAsync(ResumeConversationResponse resumed, string seal, CancellationToken cancellationToken)
    {
        // Read before anything switches: a Morgana failing here throws and leaves the user in the conversation they were in
        IReadOnlyList<MorganaChatMessage> transcript = await morganaClientService.GetHistoryAsync(resumed.ConversationId, seal, cancellationToken);

        // The conversation began with its first recorded message; one with none began, as far as anyone saw, now
        DateTimeOffset openedAt = transcript.Count > 0 ? new DateTimeOffset(transcript[0].Timestamp.ToLocalTime()) : DateTimeOffset.Now;

        string previousConversationId = session.ConversationId;
        string previousConversationSeal = session.ConversationSeal;
        session.ResumeConversation(resumed.ConversationId, seal, openedAt);

        // The conversation left behind is closed on Morgana's side without the resumed one waiting on it:
        // ending is best-effort and never throws
        _ = morganaClientService.EndConversationAsync(previousConversationId, previousConversationSeal, cancellationToken);
        return new ConversationOnScreen(resumed.ConversationId, resumed, transcript);
    }
}
