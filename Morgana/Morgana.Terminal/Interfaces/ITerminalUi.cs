using Morgana.Contracts;
using Morgana.Terminal.Messages;
using Morgana.Terminal.Services;
using Spectre.Console;

namespace Morgana.Terminal.Interfaces;

/// <summary>The live terminal UI of a channel, as the conversation lifecycle and the commands need to see it.</summary>
// Somewhere to queue what Morgana delivers, something to hand the terminal over to and the few things a command may ask of
// the screen: how any of it is drawn is the channel's business.
public interface ITerminalUi
{
    /// <summary>Queues a delivery from Morgana for display.</summary>
    void EnqueueIncoming(ChannelMessage message);

    /// <summary>Where streamed chunks are queued; unset on a channel that declares no streaming.</summary>
    // Left unset, the chunks such a channel would never show are turned away at the webhook.
    Action<StreamChunkRequest>? ChunkSink => null;

    /// <summary>Owns the terminal until the user quits or <paramref name="cancellationToken"/> fires, sending what the user types to the conversation that <paramref name="session"/> names at the time.</summary>
    Task RunAsync(TerminalSessionService session, CancellationToken cancellationToken = default);

    /// <summary>The commands the palette would offer now, judged on the conversation on screen, in the order it lists them.</summary>
    IReadOnlyList<CommandDescriptor> AvailableCommands { get; }

    /// <summary>The conversation on screen as the header shows it now.</summary>
    TerminalSessionStatus Status { get; }

    /// <summary>Ends the live UI once the running command returns, which ends the conversation and the process.</summary>
    void RequestExit();

    /// <summary>Opens the panel reporting what the command just run came to, titled with its name, with <paramref name="isFailure"/> marking what went wrong.</summary>
    // A panel as ShowPanel opens and never the transcript, since a command is not a turn of the conversation.
    void ShowCommandOutcome(string text, bool isFailure = false);

    /// <summary>Opens a panel over the whole body until the user closes it with Esc or Enter, then brings transcript and prompt back as they were.</summary>
    // layOut is asked again on every frame with the width the terminal has then and returns rows of at most that many cells.
    void ShowPanel(Func<int, IReadOnlyList<Markup>> layOut);

    /// <summary>Shows how far a command running here has got, in the widget through which a command run on Morgana reports itself.</summary>
    // Frames replace one another and none reaches the transcript. The widget goes when the command returns, so a last frame
    // marked finished is a courtesy rather than a duty.
    void ShowProgress(CommandProgress frame);

    /// <summary>Puts the conversation that <paramref name="openConversation"/> opens on screen, starting transcript, speaker, gauge and spent state over.</summary>
    // Deliveries are held while openConversation runs and the prompt then waits for the new conversation's first delivery.
    // If it throws, the screen is left as it was and the exception propagates.
    Task ReplaceConversationAsync(Func<CancellationToken, Task<string>> openConversation, CancellationToken cancellationToken);
}