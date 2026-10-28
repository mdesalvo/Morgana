using Microsoft.Extensions.Configuration;
using Morgana.Contracts;
using Morgana.Terminal.Interfaces;
using Morgana.Terminal.Messages;
using Spectre.Console;

namespace Morgana.Terminal.Services;

/// <summary>
/// The typewriter of a channel that declares streaming: chunks pile up as they arrive and are revealed at a steady
/// pace, so the reply reads as being written. A final message that lands while text is still being revealed is held
/// until the reveal ends, so the transcript never snaps ahead of what the user has read. Its timer takes the UI's
/// render lock before touching anything, every other call must already be under it.
/// </summary>
public sealed class StreamingPaneService
{
    /// <summary>Fallback for the reveal cadence when the setting is absent or non-positive, as Cauldron's.</summary>
    private const int DefaultTickMilliseconds = 15;

    /// <summary>Fallback for the characters revealed per tick when the setting is absent or non-positive, as Cauldron's.</summary>
    private const int DefaultTickChars = 1;

    /// <summary>Draws the revealed text as rows in the channel's own way.</summary>
    private readonly IMessageRenderer messageRenderer;

    /// <summary>Supplies the colour the reply being written is drawn in.</summary>
    private readonly TerminalTheme theme;

    /// <summary>Reveal cadence in milliseconds, read once at startup.</summary>
    private readonly int tickMilliseconds;

    /// <summary>Characters revealed per tick, read once at startup.</summary>
    private readonly int tickChars;

    /// <summary>Final messages held back until the reveal ends, in arrival order, so a trailing warning never overwrites the reply that carries the quick replies.</summary>
    private readonly Queue<ChannelMessage> heldFinals = new();

    /// <summary>The render lock of the UI that owns this pane, which the timer takes before touching anything. Set when the UI starts.</summary>
    private Lock? renderLock;

    /// <summary>Asks the UI to repaint the screen. Set when the UI starts.</summary>
    private Action? requestRepaint;

    /// <summary>Hands a held final message to the UI to be committed to the transcript. Set when the UI starts.</summary>
    private Action<ChannelMessage>? commitFinal;

    /// <summary>Chunks received and not yet revealed.</summary>
    private string pendingText = string.Empty;

    /// <summary>The text revealed so far, which is what the pane draws.</summary>
    private string revealedText = string.Empty;

    /// <summary>True once a final message has been held: no more chunks are coming, so the timer commits as soon as the pending text is revealed.</summary>
    private bool finalHeld;

    /// <summary>The running timer; null when no reveal is under way.</summary>
    private Timer? revealTimer;

    /// <summary>
    /// Names the reveal the running timer belongs to. Disposing a timer does not recall a tick already queued,
    /// so a tick of an older reveal must find nothing to do.
    /// </summary>
    private int revealGeneration;

    /// <summary>Reads the reveal cadence and captures the renderer and the theme.</summary>
    public StreamingPaneService(IConfiguration configuration, ChannelProfile profile, IMessageRenderer messageRenderer, TerminalTheme theme)
    {
        // The collaborators the revealed text is drawn through
        this.messageRenderer = messageRenderer;
        this.theme = theme;

        // A non-positive cadence would freeze the reveal or starve the buffer, so it falls back to the defaults
        tickMilliseconds = configuration.GetValue<int?>(profile.SectionKey("StreamingResponse:TypewriterTickMilliseconds")) ?? DefaultTickMilliseconds;
        if (tickMilliseconds <= 0)
            tickMilliseconds = DefaultTickMilliseconds;
        tickChars = configuration.GetValue<int?>(profile.SectionKey("StreamingResponse:TypewriterTickChars")) ?? DefaultTickChars;
        if (tickChars <= 0)
            tickChars = DefaultTickChars;
    }

    /// <summary>Tells whether received text is still waiting to be revealed, which is a turn alive and owed the rest of its words.</summary>
    public bool IsRevealing => pendingText.Length > 0;

    /// <summary>Tells whether the pane has text on screen, which then stands where the thinking hint would.</summary>
    public bool HasRevealedText => revealedText.Length > 0;

    /// <summary>
    /// Connects the pane to the UI that owns it: the lock its timer takes, how to repaint and how to commit a held
    /// final message. Given at start rather than at construction because the UI is built after the pane.
    /// </summary>
    public void Start(Lock uiRenderLock, Action repaint, Action<ChannelMessage> commit)
    {
        renderLock = uiRenderLock;
        requestRepaint = repaint;
        commitFinal = commit;
    }

    /// <summary>Adds a chunk to the text waiting to be revealed and starts the timer if it is not running.</summary>
    public void Append(string chunk)
    {
        pendingText += chunk;

        // The first chunk of a reveal boots the timer and the following ones only enlarge the queue
        if (revealTimer is not null)
            return;

        // The first tick fires at once on the thread pool, where it takes the render lock after this caller has released it
        int generation = ++revealGeneration;
        revealTimer = new Timer(_ => Tick(generation), null, 0, tickMilliseconds);
    }

    /// <summary>
    /// Holds a final message back while text is still being revealed, to be committed when the reveal ends.
    /// False when nothing is being revealed and the message may be committed at once.
    /// </summary>
    public bool TryHoldFinal(ChannelMessage message)
    {
        // With no reveal under way the message goes straight to the transcript
        if (revealTimer is null || (pendingText.Length == 0 && revealedText.Length == 0))
            return false;

        // A queue rather than a single slot: a warning trailing the reply must not erase the reply's quick replies
        heldFinals.Enqueue(message);
        finalHeld = true;
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
    /// Ends the reveal of a reply that will never be completed and hands back the text the user did read, so
    /// the UI can keep it as what Morgana said. Held finals are dropped.
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
        pendingText = string.Empty;
        revealedText = string.Empty;
        finalHeld = false;
        heldFinals.Clear();
    }

    /// <summary>Disposes the timer, leaving none running.</summary>
    private void StopTimer()
    {
        revealTimer?.Dispose();
        revealTimer = null;
    }

    /// <summary>
    /// Reveals the next characters and repaints. Once everything has been revealed and a final message was held,
    /// the pane is cleared and the held messages are committed in order.
    /// </summary>
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
            if (!finalHeld || heldFinals.Count == 0)
                return;

            // The reveal is over: the pane gives way to the transcript, which receives every held message so none is lost
            finalHeld = false;
            revealedText = string.Empty;
            StopTimer();
            while (heldFinals.TryDequeue(out ChannelMessage? heldFinal))
                commit(heldFinal);
        }
    }
}
