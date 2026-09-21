using Morgana.Contracts;
using Morgana.Terminal.Services;

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
    /// Where incremental chunks are queued. A channel that declares no streaming leaves this unset,
    /// and the chunks it would never show are turned away at the webhook.
    /// </summary>
    Action<StreamChunkRequest>? ChunkSink => null;

    /// <summary>
    /// Owns the terminal until the user quits or <paramref name="cancellationToken"/> fires, sending what the
    /// user types to the conversation <paramref name="session"/> names, whichever that is at the time.
    /// </summary>
    Task RunAsync(TerminalSessionService session, CancellationToken cancellationToken = default);

    /// <summary>Ends the live UI once the running command returns, which ends the conversation and the process.</summary>
    void RequestExit();

    /// <summary>
    /// Opens a turn the way a typed line does: <paramref name="echo"/> appears as the user's line, the prompt
    /// waits for Morgana's reply under the channel's reply deadline and <paramref name="dispatch"/> sends the
    /// request. A dispatch that fails is reported in the transcript and gives the prompt back.
    /// </summary>
    Task SubmitTurnAsync(string echo, Func<Task> dispatch);

    /// <summary>
    /// Puts a different conversation on screen. Deliveries are held while <paramref name="openConversation"/>
    /// runs; once it returns the new id, the transcript, speaker, gauge and spent state start over and the prompt
    /// waits for the new conversation's first delivery. If it throws, the screen is left as it was and the
    /// exception propagates.
    /// </summary>
    Task ReplaceConversationAsync(Func<CancellationToken, Task<string>> openConversation, CancellationToken cancellationToken);
}
