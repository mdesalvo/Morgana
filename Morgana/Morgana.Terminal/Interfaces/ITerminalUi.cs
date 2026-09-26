using Morgana.Contracts;
using Morgana.Terminal.Services;
using Spectre.Console;

namespace Morgana.Terminal.Interfaces;

/// <summary>
/// The live terminal UI of a channel, as the conversation lifecycle and the commands need to see it:
/// somewhere to queue what Morgana delivers, something to hand the terminal over to and the few things
/// a command may ask of the screen. How any of it is drawn is the channel's business.
/// </summary>
public interface ITerminalUi
{
    /// <summary>Queues a delivery from Morgana for display.</summary>
    void EnqueueIncoming(ChannelMessage message);

    /// <summary>
    /// Where incremental chunks are queued. A channel that declares no streaming leaves this unset:
    /// the chunks it would never show are then turned away at the webhook.
    /// </summary>
    Action<StreamChunkRequest>? ChunkSink => null;

    /// <summary>
    /// Owns the terminal until the user quits or <paramref name="cancellationToken"/> fires, sending what the
    /// user types to the conversation <paramref name="session"/> names, whichever that is at the time.
    /// </summary>
    Task RunAsync(TerminalSessionService session, CancellationToken cancellationToken = default);

    /// <summary>The commands the palette would offer now, judged on the conversation on screen, in the order it lists them.</summary>
    IReadOnlyList<CommandDescriptor> AvailableCommands { get; }

    /// <summary>Ends the live UI once the running command returns, which ends the conversation and the process.</summary>
    void RequestExit();

    /// <summary>
    /// Shows <paramref name="text"/> as the outcome of the command just run, above the prompt and never in the
    /// transcript: a command is not a turn of the conversation. It stays until the user writes in the prompt or
    /// runs another command. <paramref name="isFailure"/> marks what went wrong, so the two read apart at a glance.
    /// </summary>
    void ShowCommandOutcome(string text, bool isFailure = false);

    /// <summary>
    /// Opens a panel over the whole body until the user closes it with Esc or Enter; transcript and prompt, quick replies
    /// included, then come back as they were. <paramref name="layOut"/> is asked again on every frame with the width the
    /// terminal has then and must return rows of at most that many cells.
    /// </summary>
    void ShowPanel(Func<int, IReadOnlyList<Markup>> layOut);

    /// <summary>
    /// Shows how far a command running here has got, in the same widget a command run on Morgana reports
    /// itself through. Frames replace one another and none reaches the transcript; the widget goes when the
    /// command returns, so a last frame marked finished is a courtesy rather than a duty.
    /// </summary>
    void ShowProgress(CommandProgress frame);

    /// <summary>
    /// Puts a different conversation on screen. Deliveries are held while <paramref name="openConversation"/>
    /// runs; once it returns the new id, the transcript, speaker, gauge and spent state start over and the prompt
    /// waits for the new conversation's first delivery. If it throws, the screen is left as it was and the
    /// exception propagates.
    /// </summary>
    Task ReplaceConversationAsync(Func<CancellationToken, Task<string>> openConversation, CancellationToken cancellationToken);
}
