using Microsoft.Extensions.Configuration;
using Morgana.Contracts;
using Morgana.Terminal.Interfaces;
using Morgana.Terminal.Messages;
using Spectre.Console;

namespace Morgana.Terminal.Services;

/// <summary>
/// The typewriter of a channel that declares streaming, revealing chunks at a steady pace so that the reply reads as being written.
/// A final message that lands while text is still being revealed is held until the reveal ends, so the transcript never snaps
/// ahead of what the user has read. Its timer takes the UI's render lock before touching anything, while every other call must
/// already be under it.
/// </summary>
public sealed class StreamingPaneService
{
    /// <summary>The fallback for the reveal cadence when the setting is absent or not positive, as Cauldron's.</summary>
    private const int DefaultTickMilliseconds = 15;

    /// <summary>The fallback for the characters revealed per tick when the setting is absent or not positive, as Cauldron's.</summary>
    private const int DefaultTickChars = 1;

    /// <summary>Draws the revealed text as rows in the channel's own way.</summary>
    private readonly IMessageRenderer messageRenderer;

    /// <summary>Supplies the colour the reply being written is drawn in.</summary>
    private readonly TerminalTheme theme;

    /// <summary>The reveal cadence in milliseconds, read once at startup.</summary>
    private readonly int tickMilliseconds;

    /// <summary>The characters revealed per tick, read once at startup.</summary>
    private readonly int tickChars;

    /// <summary>
    /// The final messages held back until the reveal ends, in arrival order; none held means no final has landed yet.
    /// A queue, so a warning trailing the reply never overwrites the reply that carries the quick replies.
    /// </summary>
    private readonly Queue<ChannelMessage> heldFinals = new();

    /// <summary>The render lock of the UI that owns this pane, which the timer takes before touching anything; set when the UI starts.</summary>
    private Lock? renderLock;

    /// <summary>Asks the UI to repaint the screen; set when the UI starts.</summary>
    private Action? requestRepaint;

    /// <summary>Hands a held final message to the UI to be committed to the transcript; set when the UI starts.</summary>
    private Action<ChannelMessage>? commitFinal;

    /// <summary>The chunks received and not yet revealed.</summary>
    private string pendingText = string.Empty;

    /// <summary>The text revealed so far, which is what the pane draws.</summary>
    private string revealedText = string.Empty;

    /// <summary>The running timer; null when no reveal is under way.</summary>
    private Timer? revealTimer;

    /// <summary>
    /// Names the reveal that the running timer belongs to.
    /// Disposing a timer does not recall a tick already queued, so a tick of an older reveal must find nothing to do.
    /// </summary>
    private int revealGeneration;

    /// <summary>Builds the pane on the renderer and the theme, reading the reveal cadence from configuration.</summary>
    public StreamingPaneService(IConfiguration configuration, ChannelProfile profile, IMessageRenderer messageRenderer, TerminalTheme theme)
    {
        // The collaborators the revealed text is drawn through
        this.messageRenderer = messageRenderer;
        this.theme = theme;

        // A non-positive cadence would freeze the reveal or starve the buffer, so it falls back to the defaults
        int configuredTickMilliseconds = configuration.GetValue<int?>(profile.SectionKey("StreamingResponse:TypewriterTickMilliseconds")) ?? DefaultTickMilliseconds;
        tickMilliseconds = configuredTickMilliseconds > 0 ? configuredTickMilliseconds : DefaultTickMilliseconds;
        int configuredTickChars = configuration.GetValue<int?>(profile.SectionKey("StreamingResponse:TypewriterTickChars")) ?? DefaultTickChars;
        tickChars = configuredTickChars > 0 ? configuredTickChars : DefaultTickChars;
    }

    /// <summary>Tells whether received text is still waiting to be revealed, which is a turn alive and owed the rest of its words.</summary>
    public bool IsRevealing => pendingText.Length > 0;

    /// <summary>Tells whether the pane has text on screen, which then stands where the thinking hint would.</summary>
    public bool HasRevealedText => revealedText.Length > 0;

    /// <summary>
    /// Connects the pane to the UI that owns it: the lock its timer takes, how to repaint and how to commit a held final message.
    /// Given at start rather than at construction, because the UI is built after the pane.
    /// </summary>
    public void Start(Lock uiRenderLock, Action repaint, Action<ChannelMessage> commit)
    {
        // The timer works under the UI's lock and through the UI's own repaint and commit.
        renderLock = uiRenderLock;
        requestRepaint = repaint;
        commitFinal = commit;
    }

    /// <summary>Adds a chunk to the text waiting to be revealed and starts the timer if it is not running.</summary>
    public void Append(string chunk)
    {
        // The chunk waits behind whatever is still to be revealed.
        pendingText += chunk;

        // The first chunk of a reveal boots the timer and the following ones only enlarge the queue
        if (revealTimer is not null)
            return;

        // The first tick fires at once on the thread pool, where it takes the render lock after this caller has released it
        int generation = ++revealGeneration;
        revealTimer = new Timer(_ => Tick(generation), null, 0, tickMilliseconds);
    }

    /// <summary>Holds a final message back while text is still being revealed, returning false when nothing is and the message may be committed at once.</summary>
    public bool TryHoldFinal(ChannelMessage message)
    {
        // With no reveal under way the message goes straight to the transcript
        if (revealTimer is null || (pendingText.Length == 0 && revealedText.Length == 0))
            return false;

        // The message waits for the reveal, which commits it once everything is revealed
        heldFinals.Enqueue(message);
        return true;
    }

    /// <summary>Draws the text revealed so far; no rows while nothing has been revealed.</summary>
    public List<Markup> RenderRows(int width)
    {
        // An empty pane leaves the prompt area as it is
        if (revealedText.Length == 0)
            return [];

        // The pane speaks for an agent: base Morgana answers in a single message, never in chunks
        return messageRenderer.RenderPartial(revealedText, theme.AgentColor, width);
    }

    /// <summary>
    /// Ends the reveal of a reply that will never be completed and hands back the text the user did read, dropping the held finals.
    /// The UI keeps the text handed back as what Morgana said.
    /// </summary>
    public string FlushPartial()
    {
        // What was revealed is what Morgana actually said
        string flushedText = revealedText;
        Reset();
        return flushedText;
    }

    /// <summary>Forgets everything: the timer, the text and the held finals, for a conversation that is replaced or a UI that ends.</summary>
    public void Reset()
    {
        // A tick already queued finds no timer and does nothing
        StopTimer();

        // Nothing waits to be revealed, nothing is on screen and nothing is held.
        pendingText = string.Empty;
        revealedText = string.Empty;
        heldFinals.Clear();
    }

    /// <summary>Disposes the timer, leaving none running.</summary>
    private void StopTimer()
    {
        // A disposed timer queues no further tick and a reveal can start a new one.
        revealTimer?.Dispose();
        revealTimer = null;
    }

    /// <summary>Reveals the next characters and repaints, committing the held finals in order once everything is revealed.</summary>
    private void Tick(int generation)
    {
        // A pane the UI has not started has nothing to reveal and no lock to reveal it under
        if (renderLock is not { } uiRenderLock || requestRepaint is not { } repaint || commitFinal is not { } commit)
            return;

        // The pane is only ever touched under the UI's lock
        lock (uiRenderLock)
        {
            // A tick queued before its reveal was torn down: the live display may already be gone
            if (revealTimer is null || generation != revealGeneration)
                return;

            // Text is waiting: reveal the next slice
            if (pendingText.Length > 0)
            {
                // A tick reveals at most the configured number of characters
                int charsToReveal = Math.Min(tickChars, pendingText.Length);
                revealedText += pendingText[..charsToReveal];
                pendingText = pendingText[charsToReveal..];
                repaint();
                return;
            }

            // Everything is revealed but no final has landed: more chunks may still be on the way
            if (heldFinals.Count == 0)
                return;

            // The reveal is over: the pane gives way to the transcript, which receives every held message so none is lost
            revealedText = string.Empty;
            StopTimer();
            while (heldFinals.TryDequeue(out ChannelMessage? heldFinal))
                commit(heldFinal);
        }
    }
}