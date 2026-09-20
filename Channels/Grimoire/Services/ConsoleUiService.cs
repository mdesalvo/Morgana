using System.Text;
using System.Threading.Channels;
using Morgana.Contracts;
using Morgana.Terminal.Interfaces;
using Morgana.Terminal.Services;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Grimoire.Services;

/// <summary>
/// Terminal UI on Spectre.Console LiveDisplay: sticky header (conversation id + speaker),
/// scrolling body (chat history), input buffer at bottom. Webhook messages refresh the UI;
/// keystrokes captured via Console.ReadKey. Input gated during Morgana's turn (Escape exits).
/// </summary>
public sealed class ConsoleUiService : ITerminalUi
{
    /// <summary>Color for base Morgana turns. Matches Cauldron's <c>--primary-color #8b5cf6</c>.</summary>
    private const string MorganaColor = "#8b5cf6";

    /// <summary>Color for specialised agent turns (<c>Morgana (Agent)</c>).
    /// Matches Cauldron's <c>--secondary-color #ec4899</c>.</summary>
    private const string MorganaAgentColor = "#ec4899";

    /// <summary>Color for the user's own input and committed lines.</summary>
    private const string UserColor = "white";

    /// <summary>Color for the magic-dust gauge in the sticky header. Hex matches
    /// Cauldron's <c>--primary-color #8b5cf6</c> so both channels start the gauge on
    /// the same Morgana primary purple.</summary>
    private const string DustColor = "#8b5cf6";

    /// <summary>Gauge color when remaining ≤ 30% — Cauldron's <c>.dust-meter.low</c> amber.</summary>
    private const string DustLowColor = "#f59e0b";

    /// <summary>Gauge color when remaining ≤ 10% — Cauldron's <c>.dust-meter.critical</c> red.</summary>
    private const string DustCriticalColor = "#ef4444";

    /// <summary>Color for advisory warnings (rate-limit / low-budget): orange.</summary>
    private const string WarningColor = "orange1";

    /// <summary>Color for error notices (dust exhausted / delivery error): red.</summary>
    private const string ErrorColor = "red";

    /// <summary>Fallback for <c>Grimoire:ReplyTimeoutSeconds</c> when absent or non-positive.</summary>
    private const int DefaultReplyTimeoutSeconds = 120;


    /// <summary>
    /// Source of truth: every message ever displayed, chronologically ordered, append-only.
    /// <see cref="BuildBody"/> derives the viewport on the fly by wrap-rendering the whole
    /// list into a flat row stream and taking the last <c>bodyRows</c> rows — head rows are
    /// dropped naturally as the stream grows or the terminal shrinks, the tail (warm rows)
    /// is always preserved and the sacred user prompt is rendered separately under the
    /// stream and is never sacrificed.
    /// </summary>
    private readonly List<DisplayedMessage> history = [];

    /// <summary>
    /// The rows of the first <see cref="historyRowsMessageCount"/> messages of <see cref="history"/>, wrapped at
    /// <see cref="historyRowsWidth"/>. History only grows, so a frame renders just the messages added since the
    /// last one: a typewriter tick costs the streaming pane alone, whatever the length of the conversation.
    /// </summary>
    private readonly List<IRenderable> historyRows = [];

    /// <summary>How many messages of <see cref="history"/> are already rendered into <see cref="historyRows"/>.</summary>
    private int historyRowsMessageCount;

    /// <summary>Terminal width <see cref="historyRows"/> was wrapped at; a resize to another width renders the history again.</summary>
    private int historyRowsWidth = -1;

    /// <summary>Last terminal width the frame was drawn against, kept as the fallback of <see cref="ReadViewport"/>; the 80x24 seed only ever shows if the very first frame finds no terminal.</summary>
    private int lastViewportWidth = 80;

    /// <summary>Last terminal height the frame was drawn against, kept as the fallback of <see cref="ReadViewport"/>.</summary>
    private int lastViewportHeight = 24;

    /// <summary>
    /// Single FIFO queue for everything Morgana pushes over the webhook surface — both full
    /// <see cref="ChannelMessage"/>s (<c>/morgana-hook</c>) and incremental stream chunks
    /// (<c>/morgana-hook/chunk</c>) land here as <see cref="InboundEvent"/>s. Unifying the two
    /// streams in one channel preserves the producer's emit order all the way to the drain
    /// loop: a final message can never overtake a trailing chunk and clear the streaming
    /// buffer before that chunk has been merged into it.
    /// </summary>
    private readonly Channel<InboundEvent> inbound = Channel.CreateUnbounded<InboundEvent>();


    /// <summary>Buffer holding keystrokes not yet committed with <see cref="ConsoleKey.Enter"/>.</summary>
    private string currentInput = string.Empty;

    /// <summary>
    /// Insertion caret as a <see cref="currentInput"/> char index (invariant <c>0 ≤ cursorPosition ≤ currentInput.Length</c>).
    /// Left/Right walk it through the buffer; typing inserts and Backspace/Delete remove at this point, so the user can
    /// fix a line in place instead of only chopping the tail. Reset to 0 whenever <see cref="currentInput"/> is cleared.
    /// Mutated only under <see cref="renderLock"/>, alongside <see cref="currentInput"/>.
    /// </summary>
    private int cursorPosition;

    /// <summary>
    /// Hard cap on the prompt length in UTF-16 chars. Typing past it is swallowed (←/→/Backspace/Delete
    /// stay live, so the line can still be edited down); the header counter makes the stop self-explanatory.
    /// From <c>Grimoire:MaxInputLength</c>, default 500. Also keeps the prompt short enough that it never
    /// out-grows the body cell on a normal terminal, so the caret can't scroll out of the kept tail.
    /// </summary>
    private readonly int maxInputLength;

    /// <summary>
    /// Queue of chunk deltas waiting to be revealed by the typewriter. <see cref="HandleInboundChunk"/>
    /// appends here; <see cref="TypewriterTick"/> pulls characters off the front and moves them into
    /// <see cref="streamingDisplayed"/>. Mirror of Cauldron's <c>_streamingBuffer</c>: raw stream on
    /// one side, paced reveal on the other.
    /// </summary>
    private string streamingPending = string.Empty;

    /// <summary>
    /// Text actually rendered by the live streaming pane in <see cref="BuildBody"/>. Grows
    /// <see cref="streamingTickChars"/> characters at a time at the cadence set by
    /// <see cref="streamingTickMilliseconds"/>, until <see cref="streamingPending"/> drains and
    /// — if the final <see cref="ChannelMessage"/> has already landed — the buffered turn is
    /// committed to <see cref="history"/>.
    /// </summary>
    private string streamingDisplayed = string.Empty;

    /// <summary>
    /// Set true when at least one final <see cref="ChannelMessage"/> has been enqueued while the
    /// typewriter is still draining <see cref="streamingPending"/>. Tells the timer "no more
    /// chunks are coming — drain what you have, then commit". Reset when the queue empties.
    /// </summary>
    private bool streamingComplete;

    /// <summary>
    /// Final <see cref="ChannelMessage"/>s deferred until the typewriter finishes revealing the
    /// buffered text. Captured by <see cref="HandleInboundMessage"/> when the buffer is non-empty,
    /// consumed in order by <see cref="TypewriterTick"/> once the buffer drains. A queue (not a
    /// single field) so that a trailing system_warning arriving while the agent turn is still
    /// revealing does not overwrite the agent message and erase its QuickReplies.
    /// </summary>
    private readonly Queue<ChannelMessage> pendingFinalMessages = new();

    /// <summary>Active typewriter timer, or null when no streaming session is in flight.</summary>
    private Timer? typewriterTimer;

    /// <summary>
    /// Identifies the streaming session the running timer belongs to. Disposing a timer does not recall
    /// a tick already queued, so a tick carrying an older session must find nothing to do.
    /// </summary>
    private int typewriterSession;

    /// <summary>Per-tick reveal cadence in milliseconds, read once from configuration at startup.</summary>
    private readonly int streamingTickMilliseconds;

    /// <summary>Characters revealed per typewriter tick, read once from configuration at startup.</summary>
    private readonly int streamingTickChars;

    /// <summary>Name shown in the sticky header; swaps to the agent's name mid-turn and back to <c>Morgana</c> on completion.</summary>
    private string currentSpeaker = "Morgana";

    /// <summary>Current conversation id, displayed (truncated) in the header.</summary>
    private string conversationId = string.Empty;

    /// <summary>Pre-rendered Spectre markup segment for the dust gauge, or <see cref="string.Empty"/>
    /// when dust limiting is disabled (Morgana never sent metadata). Computed eagerly under
    /// <see cref="renderLock"/> so <see cref="BuildHeader"/> can read it without taking the lock
    /// — <c>volatile</c> guarantees the reference is always seen fresh across threads.</summary>
    private volatile string _dustSegment = string.Empty;

    /// <summary>Set once the user types <c>/quit</c> or presses <see cref="ConsoleKey.Escape"/>.</summary>
    private volatile bool exitRequested;

    /// <summary>When true, keystrokes (except Esc) are swallowed and the input line shows a thinking hint. Starts true: Grimoire always waits for Morgana's presentation before the first user turn.</summary>
    private volatile bool awaitingResponse = true;

    /// <summary>
    /// Latched true when Morgana delivers the terminal dust-exhaustion notice
    /// (<c>ErrorReason == "dust_budget_exhausted"</c>). The conversation is spent and
    /// — unlike Cauldron — Grimoire cannot start a fresh one in-process, so this is a
    /// one-way door: input stays locked (only Esc works) and the prompt is replaced
    /// by an honest "quit and relaunch" hint. Never cleared. Mutated only under
    /// <see cref="renderLock"/>; volatile so <see cref="ReadKeysLoop"/> sees it
    /// without taking the lock on every polled keystroke.
    /// </summary>
    private volatile bool conversationDead;

    /// <summary>
    /// Fast lock-free gate for "quick-reply mode": true while the current turn's offered quick
    /// replies <em>are</em> the prompt (the usual text input is suspended). Set in
    /// <see cref="CommitFinalMessage"/> when a message carries quick replies, cleared once the
    /// user picks one. Volatile so <see cref="ReadKeysLoop"/> can branch on it without taking
    /// <see cref="renderLock"/> on every polled keystroke; the backing list/index below are only
    /// ever touched under the lock.
    /// </summary>
    private volatile bool quickReplyActive;

    /// <summary>Quick replies offered by the current turn while <see cref="quickReplyActive"/>, or null. Mutated/read only under <see cref="renderLock"/>.</summary>
    private IReadOnlyList<QuickReply>? activeQuickReplies;

    /// <summary>Index of the highlighted option within <see cref="activeQuickReplies"/>. Mutated/read only under <see cref="renderLock"/>.</summary>
    private int quickReplyIndex;

    /// <summary>
    /// Scrollback offset in rows: how far above the live bottom the viewport is anchored.
    /// 0 = pinned to the newest content (the default live view). Only ever non-zero while the
    /// conversation is at rest — <see cref="ReadKeysLoop"/> gates scrolling on
    /// <c>!awaitingResponse</c>, so the window never moves under an in-flight stream — and it is
    /// reset to 0 whenever the user sends. Mutated/read only under <see cref="renderLock"/>; the
    /// upper bound is re-clamped against the live content height in <see cref="BuildBody"/>.
    /// </summary>
    private int scrollOffset;

    /// <summary>Whether older content exists above the visible window (drives the header's ▲ glyph). Set in <see cref="BuildBody"/>, read in <see cref="BuildHeader"/>; both under <see cref="renderLock"/>.</summary>
    private bool scrollHasAbove;

    /// <summary>Whether content exists below the visible window — i.e. the user has scrolled up (drives the header's ▼ glyph). Set in <see cref="BuildBody"/>, read in <see cref="BuildHeader"/>.</summary>
    private bool scrollHasBelow;

    /// <summary>Platform-specific terminal-resize notifier; subscribed in <see cref="RunAsync"/> so the viewport anchor follows live window resizes without per-frame polling.</summary>
    private readonly IViewportResizeWatcher viewportResizeWatcher;

    /// <summary>Renders chat/streaming text to Spectre rows — holds the cell-width service that keeps a wide glyph from desyncing the row budget the rest of this class relies on.</summary>
    private readonly MarkdownTerminalRenderService markdownRenderer;

    /// <summary>Renders a <see cref="RichCard"/> to a bordered Spectre box.</summary>
    private readonly RichCardTerminalRenderService richCardRenderer;

    /// <summary>Renders a turn's quick replies to the selectable prompt surface.</summary>
    private readonly QuickReplyTerminalRenderService quickReplyRenderer;

    /// <summary>
    /// How long a turn may go completely silent before the prompt is handed back to the user. It measures
    /// silence, not the length of the turn: a streamed reply pushes the deadline back with every chunk,
    /// so a long answer that is still arriving is never declared lost. From <c>Grimoire:ReplyTimeoutSeconds</c>.
    /// </summary>
    private readonly TimeSpan replyTimeout;

    /// <summary>Ends the countdown on the turn in flight; null when nothing is being waited for. Mutated only under <see cref="renderLock"/>.</summary>
    private CancellationTokenSource? replyWatchdog;

    /// <summary>Fires when the host stops, so a deadline armed mid-turn dies with the process rather than outliving the live display.</summary>
    private CancellationToken uiStopping;

    /// <summary>Serializes mutations of <see cref="history"/>, <see cref="currentInput"/>, <see cref="currentSpeaker"/> and the paired <see cref="LiveDisplayContext.UpdateTarget"/>/<see cref="LiveDisplayContext.Refresh"/> calls. The resize callback runs on its own thread (SIGWINCH handler / polling task) and would otherwise race with <see cref="ReadKeysLoop"/> and <see cref="DrainInboundLoop"/> over the shared list. Uses <see cref="System.Threading.Lock"/> (.NET 9+) instead of <c>object</c> so the compiler emits the optimised primitive and rejects misuse (e.g. passing the lock as an <c>object</c>).</summary>
    private readonly Lock renderLock = new();

    /// <summary>Reads the typewriter cadence settings and captures the injected resize watcher and the three DI-registered renderers.</summary>
    public ConsoleUiService(
        IConfiguration configuration,
        IViewportResizeWatcher viewportResizeWatcher,
        MarkdownTerminalRenderService markdownRenderer,
        RichCardTerminalRenderService richCardRenderer,
        QuickReplyTerminalRenderService quickReplyRenderer)
    {
        // A non-positive wait would declare every turn lost the instant it is sent, so it falls back too
        int replyTimeoutSeconds = configuration.GetValue<int?>("Grimoire:ReplyTimeoutSeconds") ?? DefaultReplyTimeoutSeconds;
        replyTimeout = TimeSpan.FromSeconds(replyTimeoutSeconds > 0 ? replyTimeoutSeconds : DefaultReplyTimeoutSeconds);
        this.viewportResizeWatcher = viewportResizeWatcher;
        this.markdownRenderer = markdownRenderer;
        this.richCardRenderer = richCardRenderer;
        this.quickReplyRenderer = quickReplyRenderer;

        // Typewriter cadence — mirror of Cauldron's Cauldron:StreamingResponse:Typewriter* keys.
        // Non-positive values fall back to the Cauldron defaults (15 ms, 1 char/tick) so a
        // misconfiguration can't freeze the reveal or starve the buffer.
        streamingTickMilliseconds = configuration.GetValue<int?>("Grimoire:StreamingResponse:TypewriterTickMilliseconds") ?? 15;
        if (streamingTickMilliseconds <= 0)
            streamingTickMilliseconds = 15;
        streamingTickChars = configuration.GetValue<int?>("Grimoire:StreamingResponse:TypewriterTickChars") ?? 1;
        if (streamingTickChars <= 0)
            streamingTickChars = 1;

        // Non-positive (or absent) falls back to 500 so a misconfiguration can't lock the prompt shut.
        maxInputLength = configuration.GetValue<int?>("Grimoire:MaxInputLength") ?? 500;
        if (maxInputLength <= 0)
            maxInputLength = 500;
    }

    /// <summary>Called by <see cref="WebhookReceiverService"/> when Morgana delivers a message.</summary>
    public void EnqueueIncoming(ChannelMessage message) => inbound.Writer.TryWrite(new MessageEvent(message));

    /// <summary>Called by <see cref="WebhookReceiverService"/> when Morgana delivers a streaming chunk.</summary>
    public void EnqueueChunk(string chunkText) => inbound.Writer.TryWrite(new ChunkEvent(chunkText));

    /// <summary>Grimoire renders chunks, so every delta Morgana streams reaches the typewriter pane.</summary>
    public Action<StreamChunkRequest>? ChunkSink => chunk => EnqueueChunk(chunk.ChunkText);

    /// <summary>
    /// Starts the live terminal UI. Returns when the user types <c>/quit</c> or the cancellation
    /// token fires. <paramref name="onSend"/> is invoked on each committed user input line.
    /// </summary>
    public async Task RunAsync(
        string convId,
        Func<string, Task> onSend,
        CancellationToken cancellationToken = default)
    {
        // Lock even here (pre-threading, zero contention) so all fields read by
        // BuildLayout/BuildHeader/AppendHistoryTail are always accessed under renderLock —
        // keeps Rider's inconsistent-sync analysis clean.
        Layout layout;
        lock (renderLock)
        {
            conversationId = convId;
            layout = BuildLayout();
        }

        // Spectre's Live rendering only ever writes/diffs content — it never touches the terminal's
        // own native cursor. Left visible, that cursor sits wherever the last partial repaint's
        // cursor-position escape happened to leave it (often the start of whatever row Spectre wrote
        // last), showing up as a stray blinking block in the middle of the scrollback that has
        // nothing to do with the blinking "_"/inverted-block caret Grimoire draws itself at the
        // prompt. Hidden for the whole Live session, restored in the finally below so the user's
        // shell prompt gets its cursor back on exit.
        AnsiConsole.Cursor.Hide();
        try
        {
            await RunLiveDisplayAsync(layout, onSend, cancellationToken);
        }
        finally
        {
            AnsiConsole.Cursor.Show();
        }
    }

    /// <summary>The actual Live(Layout) session — split out of <see cref="RunAsync"/> so the cursor hide/show wrap above reads as a single, obvious guard rather than being buried inside the lambda.</summary>
    private async Task RunLiveDisplayAsync(Layout layout, Func<string, Task> onSend, CancellationToken cancellationToken)
    {
        await AnsiConsole
            .Live(layout)
            .AutoClear(false)
            .Overflow(VerticalOverflow.Ellipsis)
            .Cropping(VerticalOverflowCropping.Top)
            .StartAsync(async ctx =>
            {
                // Subscribed inside StartAsync because the callback needs the live
                // LiveDisplayContext to push UpdateTarget+Refresh; the `using`
                // unsubscribes when the UI loop exits (normal quit, Esc, cancel,
                // or PTY death), so the SIGWINCH handler / polling task doesn't
                // outlive the Live display.
                using IDisposable resizeSubscription = viewportResizeWatcher.Subscribe(() =>
                {
                    lock (renderLock)
                    {
                        ctx.UpdateTarget(BuildLayout());
                        ctx.Refresh();
                    }
                });

                // Paint the opening frame. Morgana's first delivery is what normally brings the screen
                // to life. A backend too slow for the startup wait, or unable to reach the callback,
                // would otherwise leave the user staring at a blank terminal with no header, no
                // conversation id and no sign that Grimoire is waiting on anything.
                lock (renderLock)
                    ctx.Refresh();

                uiStopping = cancellationToken;

                Task readLoop = Task.Run(() => ReadKeysLoop(ctx, onSend, cancellationToken), cancellationToken);
                Task drainLoop = Task.Run(() => DrainInboundLoop(ctx, cancellationToken), cancellationToken);
                try
                {
                    await Task.WhenAny(readLoop, drainLoop);
                }
                finally
                {
                    // Tear down any typewriter still ticking when the UI loop exits — otherwise
                    // the threadpool callback outlives the Live display and would call back into
                    // a stale LiveDisplayContext.
                    lock (renderLock)
                        StopTypewriter();
                }
            });
    }

    /// <summary>
    /// Consumes the unified <see cref="inbound"/> channel and dispatches each event by type:
    /// <see cref="MessageEvent"/>s feed into <see cref="HandleInboundMessage"/> (history append +
    /// streaming buffer clear), <see cref="ChunkEvent"/>s feed into <see cref="HandleInboundChunk"/>
    /// (streaming buffer append). One drain loop = strictly FIFO processing: a trailing chunk that
    /// arrives just before the final message can never be reordered after it.
    /// </summary>
    private async Task DrainInboundLoop(LiveDisplayContext ctx, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (InboundEvent ev in inbound.Reader.ReadAllAsync(cancellationToken))
            {
                switch (ev)
                {
                    case ChunkEvent { ChunkText: var chunkText }:
                        HandleInboundChunk(ctx, chunkText);
                        break;
                    case MessageEvent { Message: var message }:
                        if (HandleInboundMessage(ctx, message))
                            return;
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Cancellation is the normal exit path — swallow.
        }
    }

    /// <summary>
    /// Appends a chunk delta to <see cref="streamingPending"/> and ensures the typewriter timer
    /// is running. The first chunk of a turn boots the timer; subsequent chunks just enlarge the
    /// queue. No UI refresh happens here — the timer ticks at <see cref="streamingTickMilliseconds"/>
    /// cadence and refreshes after each reveal. Empty chunks are no-ops.
    /// </summary>
    private void HandleInboundChunk(LiveDisplayContext ctx, string chunkText)
    {
        if (chunkText.Length == 0)
            return;

        lock (renderLock)
        {
            streamingPending += chunkText;

            // A chunk is Morgana answering, so the silence the deadline measures starts over here
            if (awaitingResponse)
                StartReplyWatchdog(ctx);

            EnsureTypewriterStarted(ctx);
        }
    }

    /// <summary>Boots <see cref="typewriterTimer"/> if it isn't already running. Idempotent; safe to call on every chunk.</summary>
    private void EnsureTypewriterStarted(LiveDisplayContext ctx)
    {
        if (typewriterTimer is not null)
            return;

        // The first tick fires at once on the thread pool, where it takes renderLock after this caller has released it
        int session = ++typewriterSession;
        typewriterTimer = new Timer(_ => TypewriterTick(ctx, session), null, 0, streamingTickMilliseconds);
    }

    /// <summary>
    /// Reveals up to <see cref="streamingTickChars"/> characters from <see cref="streamingPending"/>
    /// into <see cref="streamingDisplayed"/> and refreshes the UI. When the buffer drains and finals
    /// have arrived (<see cref="streamingComplete"/>), commits all deferred
    /// <see cref="pendingFinalMessages"/> in order and tears the session down.
    /// </summary>
    /// <remarks>The commit happens under the same lock as an arriving message, so a message landing meanwhile is committed after the reply it follows.</remarks>
    private void TypewriterTick(LiveDisplayContext ctx, int session)
    {
        lock (renderLock)
        {
            // A tick queued before its session was torn down: the live display may already be gone
            if (typewriterTimer is null || session != typewriterSession)
                return;

            if (streamingPending.Length > 0)
            {
                int charsToTake = Math.Min(streamingTickChars, streamingPending.Length);
                streamingDisplayed += streamingPending[..charsToTake];
                streamingPending = streamingPending[charsToTake..];
                ctx.UpdateTarget(BuildLayout());
                ctx.Refresh();
                return;
            }

            // Buffer is empty. If finals have landed, drain the whole queue so no message is lost.
            // Idle otherwise — more chunks may still be on the way.
            if (!streamingComplete || pendingFinalMessages.Count == 0)
                return;

            streamingComplete = false;
            streamingDisplayed = string.Empty;
            StopTypewriter();
            while (pendingFinalMessages.TryDequeue(out ChannelMessage? pendingFinalMessage))
                CommitFinalMessage(ctx, pendingFinalMessage);
        }
    }

    /// <summary>Disposes <see cref="typewriterTimer"/> and clears the reference. Must be called under <see cref="renderLock"/>.</summary>
    private void StopTypewriter()
    {
        typewriterTimer?.Dispose();
        typewriterTimer = null;
    }

    /// <summary>
    /// Routes a final <see cref="ChannelMessage"/>: if a typewriter session is in flight
    /// (buffer not yet drained), defers the commit so the user sees the reveal complete
    /// naturally before history snaps; otherwise commits immediately. Returns true when an
    /// exit was requested mid-turn so the drain loop breaks after the last frame.
    /// </summary>
    private bool HandleInboundMessage(LiveDisplayContext ctx, ChannelMessage message)
    {
        lock (renderLock)
        {
            // Morgana has spoken, so the turn is no longer at risk of being declared lost — called
            // on arrival, not on commit, since the typewriter may hold the text for a while yet
            CancelReplyWatchdog();

            if (typewriterTimer is not null && (streamingPending.Length > 0 || streamingDisplayed.Length > 0))
            {
                // Typewriter still revealing: enqueue the final and let the timer commit when
                // streamingPending drains. Using a queue (not a single field) ensures that a
                // trailing system_warning arriving in the same window does not overwrite an
                // agent message that carries QuickReplies — both are committed in order.
                pendingFinalMessages.Enqueue(message);
                streamingComplete = true;
                return exitRequested;
            }

            // No active typewriter — go straight to history.
            CommitFinalMessage(ctx, message);
            return exitRequested;
        }
    }

    /// <summary>
    /// Commits a final <see cref="ChannelMessage"/> to history, updates the dust gauge / header /
    /// dead-state latch and refreshes the live view. Invoked either inline by
    /// <see cref="HandleInboundMessage"/> (no streaming) or by <see cref="TypewriterTick"/> once
    /// the buffer drains. Must be called under <see cref="renderLock"/>.
    /// </summary>
    private void CommitFinalMessage(LiveDisplayContext ctx, ChannelMessage message)
    {
        // Attribute the row to whoever authored it: a specialised agent keeps its
        // own colour even on the farewell line that carries AgentCompleted=true.
        // The name comes over the unauthenticated callback, so it is cleaned once here and every
        // place that shows it afterwards — row prefix, header, courtesy line — reuses this value.
        string messageSpeaker = string.IsNullOrWhiteSpace(message.AgentName)
            ? "Morgana"
            : TerminalCellService.StripControlCharacters(message.AgentName);

        history.Add(new DisplayedMessage(messageSpeaker, message.Text, RowColor(message, messageSpeaker), message.RichCard));

        // Revert the sticky header to Morgana on completion so the next user
        // turn doesn't render under the outgoing agent's colour.
        currentSpeaker = message.AgentCompleted || string.IsNullOrWhiteSpace(message.AgentName)
            ? "Morgana"
            : messageSpeaker;

        // Refresh the header gauge from ANY metadata-bearing message. The main
        // assistant response carries the pre-delivery estimate; the trailing
        // warning/exhaustion (same turn, moments later) carries the AUTHORITATIVE
        // post-send level. For Grimoire's full capability profile the channel
        // adapter short-circuits and the two values usually coincide, but we still
        // honour the trailing reading so that any future change (an experimental
        // server-side post-processing pass, a future Grimoire variant with a tighter
        // budget) snaps the gauge to the truthful post-send number without any code
        // change here. Spectre's diffing makes an unchanged segment an invisible no-op.
        if (message.ConversationMetadata?.DustLevel is { } level)
        {
            // Truncate toward zero, don't round: a sub-1% residual reads as 0% — that
            // swallowed fraction is the slack that funds per-channel presentation
            // messages and the let-it-finish turn.
            int dustLevel = Math.Clamp((int)(level * 100), 0, 100);
            // Scale color with depletion: mirrors Cauldron DustMeter thresholds (>30% ok, >10% low, ≤10% critical).
            string dustColor = level > 0.30 ? DustColor : level > 0.10 ? DustLowColor : DustCriticalColor;
            _dustSegment = $"   [grey54]dust[/] [bold {dustColor}]{dustLevel}%[/]";
        }

        // Terminal lockout. Morgana proactively pushes this at end of turn
        // (same ErrorReason as the doomed-next-send path), so the user sees
        // it BEFORE wasting a keystroke. Morgana's text says "start a new
        // one to keep going" — true for Cauldron, NOT for Grimoire, which has no
        // in-process restart. So latch a one-way dead state: the red banner
        // line above stays as Morgana's canonical word and BuildInputRows
        // overrides the prompt with a Grimoire-honest "quit and relaunch" hint.
        if (string.Equals(message.ErrorReason, "dust_budget_exhausted", StringComparison.Ordinal))
        {
            conversationDead = true;
            currentInput = string.Empty; // discard any half-typed doomed line
            cursorPosition = 0;
            // Tear down any quick replies a same-turn agent message already activated:
            // the dead latch suppresses them on both the render and input gates anyway,
            // but clearing the backing state makes "game over" explicit rather than masked.
            quickReplyActive = false;
            activeQuickReplies = null;
        }

        // Quick replies turn the bottom line INTO the prompt for this turn: instead of
        // freeing the text input, enter "QR mode" where the offered options ARE the prompt
        // and ReadKeysLoop drives a selection over them. Mirrors Cauldron locking its
        // textarea while quick replies are pending. Suppressed once the conversation is
        // dust-dead — the dead latch wins, there's nothing left to branch into.
        if (!conversationDead && message.QuickReplies is { Count: > 0 })
        {
            activeQuickReplies = message.QuickReplies;
            quickReplyIndex = 0;
            quickReplyActive = true;
        }

        // Release the input gate: ReadKeysLoop was swallowing keystrokes until
        // this first webhook delivery landed. (No-op once conversationDead:
        // ReadKeysLoop keeps swallowing on the dead latch regardless. In QR mode the
        // text input stays suspended too, but via quickReplyActive, not this flag.)
        awaitingResponse = false;
        ctx.UpdateTarget(BuildLayout());
        ctx.Refresh();
    }

    /// <summary>Polls <see cref="Console.KeyAvailable"/> every 25 ms and dispatches keys: in quick-reply mode the arrows move the highlight and Enter sends the choice; otherwise Enter commits (or exits on <c>/quit</c>), Backspace edits, Esc exits, printable chars append to the buffer.</summary>
    private async Task ReadKeysLoop(LiveDisplayContext ctx, Func<string, Task> onSend, CancellationToken cancellationToken)
    {
        while (!exitRequested && !cancellationToken.IsCancellationRequested)
        {
            // Polled rather than blocking: Spectre.Console's Live rendering cannot share
            // stdin with a first-class prompt, so we spin on KeyAvailable.
            //
            // The try/catch is the safety net for "PTY died without a signal": if the
            // host terminal closes and SIGHUP doesn't reach us (or arrives late), the
            // next stdin op throws IOException / InvalidOperationException — treat it
            // as an exit so the process can terminate and docker's --rm reclaims the
            // container instead of leaving it attached to morgana-network.
            ConsoleKeyInfo key;
            try
            {
                if (!Console.KeyAvailable)
                {
                    await Task.Delay(25, cancellationToken);
                    continue;
                }

                key = Console.ReadKey(intercept: true);
            }
            catch (OperationCanceledException)
            {
                // Shutdown was asked for (Ctrl+C, SIGTERM, SIGHUP): close the delivery stream so the
                // conversation loop returns as well. The exit flag stays untouched — nobody typed /quit.
                inbound.Writer.TryComplete();
                return;
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                exitRequested = true;
                inbound.Writer.TryComplete();
                return;
            }

            // Scrollback: review the finished conversation. Enabled only at rest — NOT in
            // quick-reply mode (the QR prompt is sacred and must be answered first) and NOT while
            // a turn is in flight (awaitingResponse covers both "thinking" and streaming; you only
            // scroll content that's done). Handled before the swallow gate below so it still works
            // once the conversation is dust-dead (awaitingResponse is false there) for re-reading.
            if (!quickReplyActive && !awaitingResponse && TryScrollDelta(key.Key, out int scrollDelta))
            {
                ApplyScroll(ctx, scrollDelta);
                continue;
            }

            // Swallow every keystroke that isn't an explicit exit while we're waiting for
            // Morgana to speak — or forever once the conversation is dust-dead (a
            // one-way latch: no point typing into a budget the backend will reject).
            // Esc is always honoured so the user can bail out even mid-turn or quit a
            // spent conversation; everything else (printable chars, Enter, Backspace)
            // is a no-op.
            // Lock-free read is intentional: awaitingResponse and conversationDead are both
            // volatile, guaranteeing visibility. Taking renderLock here on every polled
            // keystroke would cause unnecessary contention with DrainIncomingLoop.
            // ReSharper disable InconsistentlySynchronizedField
            if ((awaitingResponse || conversationDead) && key.Key != ConsoleKey.Escape)
            // ReSharper restore InconsistentlySynchronizedField
                continue;

            // Quick-reply mode owns the keyboard: the offered options ARE the prompt, so the
            // arrows move the highlight and Enter sends the chosen option's Value. Esc is left to
            // fall through to the switch below (it always exits); every other key is consumed here
            // — no free-text typing while a branch choice is pending (Cauldron parity). The fast
            // gate is the volatile flag; HandleQuickReplyKeyAsync re-checks the backing list under
            // the lock to stay safe against a concurrent clear.
            if (quickReplyActive && key.Key != ConsoleKey.Escape)
            {
                await HandleQuickReplyKeyAsync(ctx, onSend, key.Key);
                continue;
            }

            switch (key.Key)
            {
                case ConsoleKey.Enter:
                {
                    // Snapshot-and-clear before awaiting: any late keystroke during onSend
                    // must land on a fresh buffer, not reappend to the line we just sent.
                    // Length check is inside the lock — DrainIncomingLoop also writes
                    // currentInput (under lock), so the when-guard read would be a cross-thread
                    // race if left outside.
                    string toSend;
                    lock (renderLock)
                    {
                        toSend = currentInput;
                        currentInput = string.Empty;
                        cursorPosition = 0;
                    }
                    if (toSend.Length == 0) break;

                    // /quit is a client-only command: never round-trip it to Morgana.
                    if (toSend.Equals("/quit", StringComparison.OrdinalIgnoreCase))
                    {
                        exitRequested = true;
                        inbound.Writer.TryComplete();
                        return;
                    }

                    // Optimistic echo: show "You: …" and flip to the thinking hint before
                    // awaiting onSend so the UI feels responsive even on slow backends.
                    lock (renderLock)
                    {
                        history.Add(new DisplayedMessage("You", toSend, UserColor));
                        awaitingResponse = true;
                        scrollOffset = 0; // jump back to the live bottom for the new turn
                        ctx.UpdateTarget(BuildLayout());
                        ctx.Refresh();
                    }

                    try
                    {
                        await onSend(toSend);

                        // Morgana took the message: from here only its reply can reopen the prompt,
                        // so give that reply a deadline. A turn already answered has nothing to watch.
                        lock (renderLock)
                        {
                            if (awaitingResponse)
                                StartReplyWatchdog(ctx);
                        }
                    }
                    catch (Exception ex)
                    {
                        // Surface the failure in-UI (logging is silenced) and release the
                        // gate so the user can retry without waiting for a webhook that
                        // will never arrive.
                        lock (renderLock)
                        {
                            history.Add(new DisplayedMessage("system", $"send failed: {ex.Message}", "red"));
                            awaitingResponse = false;
                            ctx.UpdateTarget(BuildLayout());
                            ctx.Refresh();
                        }
                    }
                    break;
                }
                case ConsoleKey.Backspace:
                    lock (renderLock)
                    {
                        // Delete the char to the LEFT of the caret (and step the caret back over it),
                        // not the tail: with an in-place caret the two only coincide at end-of-line.
                        if (cursorPosition > 0)
                        {
                            currentInput = currentInput.Remove(cursorPosition - 1, 1);
                            cursorPosition--;
                            RefreshWhenInputSettles(ctx);
                        }
                    }
                    break;
                case ConsoleKey.Delete:
                    lock (renderLock)
                    {
                        // Forward delete: remove the char UNDER the caret, leaving the caret put.
                        if (cursorPosition < currentInput.Length)
                        {
                            currentInput = currentInput.Remove(cursorPosition, 1);
                            RefreshWhenInputSettles(ctx);
                        }
                    }
                    break;
                case ConsoleKey.LeftArrow:
                    lock (renderLock)
                    {
                        if (cursorPosition > 0)
                        {
                            cursorPosition--;
                            RefreshWhenInputSettles(ctx);
                        }
                    }
                    break;
                case ConsoleKey.RightArrow:
                    lock (renderLock)
                    {
                        if (cursorPosition < currentInput.Length)
                        {
                            cursorPosition++;
                            RefreshWhenInputSettles(ctx);
                        }
                    }
                    break;
                case ConsoleKey.Escape:
                    // Unconditional exit — completes the channel so DrainIncomingLoop
                    // unblocks out of its await foreach and RunAsync can return.
                    exitRequested = true;
                    inbound.Writer.TryComplete();
                    return;
                default:
                    // Skip control chars (arrows, F-keys, …); only printable glyphs feed
                    // the buffer. Layout must refresh on every keystroke or the cursor
                    // lags behind what the user just typed.
                    if (!char.IsControl(key.KeyChar))
                    {
                        lock (renderLock)
                        {
                            // Hard cap: once the buffer is full, swallow further glyphs. Length is read
                            // under the lock (DrainIncomingLoop also mutates currentInput) to avoid a
                            // cross-thread race. Caret moves and deletions stay live, so a maxed-out line
                            // can still be trimmed; the header N/max counter explains the stop.
                            if (currentInput.Length < maxInputLength)
                            {
                                // Insert AT the caret (not append) so typing mid-line splices in place.
                                currentInput = currentInput.Insert(cursorPosition, key.KeyChar.ToString());
                                cursorPosition++;
                                RefreshWhenInputSettles(ctx);
                            }
                        }
                    }
                    break;
            }
        }
    }

    /// <summary>
    /// Handles a keystroke while in quick-reply mode. Up/Left and Down/Right wrap the highlight
    /// (both axes accepted so the user needn't guess which the current layout wants); Enter leaves
    /// QR mode, echoes the chosen option as a <c>"You: {Value}"</c> line — Cauldron sends the
    /// <see cref="QuickReply.Value"/>, not the label — and dispatches it through
    /// <paramref name="onSend"/>; any other key is ignored. All state mutation happens under
    /// <see cref="renderLock"/>; the send awaits outside it.
    /// </summary>
    private async Task HandleQuickReplyKeyAsync(LiveDisplayContext ctx, Func<string, Task> onSend, ConsoleKey key)
    {
        QuickReply? chosen = null;
        lock (renderLock)
        {
            // Re-check under the lock: the gate is volatile but the list could have been cleared
            // by a racing commit between the loop's fast read and here.
            if (activeQuickReplies is not { Count: > 0 } options)
                return;

            switch (key)
            {
                case ConsoleKey.UpArrow or ConsoleKey.LeftArrow:
                    quickReplyIndex = (quickReplyIndex - 1 + options.Count) % options.Count;
                    break;
                case ConsoleKey.DownArrow or ConsoleKey.RightArrow:
                    quickReplyIndex = (quickReplyIndex + 1) % options.Count;
                    break;
                case ConsoleKey.Enter:
                    chosen = options[quickReplyIndex];
                    // Leave QR mode and optimistically echo the choice, exactly as the text-input
                    // Enter path does — the bottom line flips to the thinking hint while we send.
                    quickReplyActive = false;
                    activeQuickReplies = null;
                    history.Add(new DisplayedMessage("You", chosen.Value, UserColor));
                    awaitingResponse = true;
                    scrollOffset = 0; // back to live for the new turn (already 0 in QR mode, but keep it explicit)
                    break;
                default:
                    return; // non-navigation key: nothing changed, skip the refresh
            }

            ctx.UpdateTarget(BuildLayout());
            ctx.Refresh();
        }

        if (chosen is null)
            return;

        try
        {
            await onSend(chosen.Value);

            // A chosen quick reply is a turn like any other and deserves the same deadline
            lock (renderLock)
            {
                if (awaitingResponse)
                    StartReplyWatchdog(ctx);
            }
        }
        catch (Exception ex)
        {
            // Same recovery as the text path: surface the failure and release the gate so the
            // user can act again (the quick replies are gone, but free text is available).
            lock (renderLock)
            {
                history.Add(new DisplayedMessage("system", $"send failed: {ex.Message}", "red"));
                awaitingResponse = false;
                ctx.UpdateTarget(BuildLayout());
                ctx.Refresh();
            }
        }
    }

    /// <summary>
    /// Gives the turn in flight <see cref="replyTimeout"/> of silence before the user gets the prompt back
    /// with an honest notice. Every sign of life — a chunk, a message — arms it again from zero, so only a
    /// turn Morgana has genuinely abandoned expires. Must be called under <see cref="renderLock"/>.
    /// </summary>
    private void StartReplyWatchdog(LiveDisplayContext ctx)
    {
        CancelReplyWatchdog();
        CancellationTokenSource watchdog = CancellationTokenSource.CreateLinkedTokenSource(uiStopping);
        replyWatchdog = watchdog;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(replyTimeout, watchdog.Token);
            }
            catch (OperationCanceledException)
            {
                // Morgana spoke in time, or the process is going down: whoever called the deadline off owns it
                return;
            }

            lock (renderLock)
            {
                // The deadline has expired, so the next turn must not find it still standing: a turn that
                // ends up calling off a deadline already spent would take the conversation down with it
                if (ReferenceEquals(replyWatchdog, watchdog))
                {
                    replyWatchdog = null;
                    watchdog.Dispose();
                }

                // A reply that landed while the deadline was expiring wins: the prompt is already back
                if (!awaitingResponse || conversationDead)
                    return;

                // The typewriter is still revealing what Morgana already sent: the turn is alive and
                // owed the rest of its text, however long the reveal takes
                if (streamingPending.Length > 0)
                {
                    StartReplyWatchdog(ctx);
                    return;
                }

                // What the stream did reveal is what Morgana actually said, so it is kept as the turn's
                // reply rather than left hanging in a pane the next turn would write underneath
                if (streamingDisplayed.Length > 0)
                {
                    history.Add(new DisplayedMessage(currentSpeaker, streamingDisplayed, SpeakerColor(currentSpeaker)));
                    streamingDisplayed = string.Empty;
                }
                streamingComplete = false;
                pendingFinalMessages.Clear();
                StopTypewriter();

                history.Add(new DisplayedMessage(
                    "system",
                    $"no answer from Morgana after {replyTimeout.TotalSeconds:0}s — the turn may be lost, ask again or press Esc to quit",
                    ErrorColor));
                awaitingResponse = false;
                ctx.UpdateTarget(BuildLayout());
                ctx.Refresh();
            }
        }, CancellationToken.None);
    }

    /// <summary>Calls off the deadline on the turn in flight, leaving none armed. A deadline that already expired retired itself. Must be called under <see cref="renderLock"/>.</summary>
    private void CancelReplyWatchdog()
    {
        replyWatchdog?.Cancel();
        replyWatchdog?.Dispose();
        replyWatchdog = null;
    }

    /// <summary>Number of rows the scrollback advances per keystroke. Fixed (not configurable).</summary>
    private const int ScrollStep = 5;

    /// <summary>
    /// Maps a key to a scroll direction: Up / PageUp move toward older content (+),
    /// Down / PageDown move back toward the present (−). Returns false for any other key.
    /// Only the vertical axis scrolls: Left/Right are reserved for caret movement inside the
    /// prompt line (the muscle-memory expectation in a text field), so they never reach here.
    /// </summary>
    private static bool TryScrollDelta(ConsoleKey key, out int delta)
    {
        switch (key)
        {
            case ConsoleKey.UpArrow or ConsoleKey.PageUp:
                delta = ScrollStep;
                return true;
            case ConsoleKey.DownArrow or ConsoleKey.PageDown:
                delta = -ScrollStep;
                return true;
            default:
                delta = 0;
                return false;
        }
    }

    /// <summary>Advances the scrollback by <paramref name="delta"/> rows and refreshes. Floors at the live bottom (0); the upper bound is clamped against the live content height in <see cref="BuildBody"/>.</summary>
    private void ApplyScroll(LiveDisplayContext ctx, int delta)
    {
        lock (renderLock)
        {
            scrollOffset = Math.Max(0, scrollOffset + delta);
            ctx.UpdateTarget(BuildLayout());
            ctx.Refresh();
        }
    }

    /// <summary>
    /// Current terminal size, falling back to the last one seen when the terminal no longer answers. A
    /// window closed mid-frame must not break the repaint in progress: the frame is drawn against stale
    /// but plausible dimensions and the process leaves through the ordinary SIGHUP path moments later.
    /// </summary>
    private (int Width, int Height) ReadViewport()
    {
        try
        {
            lastViewportWidth = Math.Max(1, Console.WindowWidth);
            lastViewportHeight = Math.Max(0, Console.WindowHeight);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            // Terminal disconnected: the last known size is the best answer available
        }
        return (lastViewportWidth, lastViewportHeight);
    }

    /// <summary>
    /// Repaints the screen unless further keystrokes are already queued. A pasted line reaches Grimoire
    /// one glyph at a time and only its settled state is worth showing, so a long paste costs one frame
    /// instead of one per character. A terminal that has gone away reports nothing pending, leaving the
    /// tear-down to the next read from it. Must be called under <see cref="renderLock"/>.
    /// </summary>
    private void RefreshWhenInputSettles(LiveDisplayContext ctx)
    {
        try
        {
            if (Console.KeyAvailable)
                return;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            // The terminal is gone; paint the frame anyway and let the key loop take the exit path
        }

        ctx.UpdateTarget(BuildLayout());
        ctx.Refresh();
    }

    /// <summary>Builds the two-row Spectre layout (fixed 3-row header + flex body).</summary>
    private Layout BuildLayout()
    {
        Layout root = new Layout("root")
            .SplitRows(
                new Layout("header").Size(3),
                new Layout("body").Ratio(1));

        // Body first: it recomputes the scroll flags (scrollHasAbove/Below) the header's ▲▼
        // glyphs read, so they reflect this same frame rather than lagging one behind.
        IRenderable body = BuildBody();
        root["header"].Update(BuildHeader());
        root["body"].Update(body);
        return root;
    }

    /// <summary>Renders the sticky header panel with the current speaker (colored by role) and a truncated conversation id.</summary>
    private IRenderable BuildHeader()
    {
        // Speaker colour tracks currentSpeaker, which DrainIncomingLoop flips to the agent
        // mid-turn and back to Morgana on completion — the header always mirrors "who holds
        // the mic right now".
        string speakerColor = SpeakerColor(currentSpeaker);

        // Conversation ids are 32-char hex; truncate so the header stays readable on narrow
        // terminals without forcing the panel to wrap.
        string shortId = conversationId.Length > 12 ? conversationId[..12] + "…" : conversationId;

        // Magic-dust gauge: pre-rendered under renderLock when metadata arrives; empty
        // string means dust limiting is disabled (gauge hidden). Rebuilt by BuildLayout on
        // every resize via the existing IViewportResizeWatcher callback, so it stays
        // correctly aligned without any extra resize plumbing.
        string dustSegment = _dustSegment;

        // Scrollback indicator: ▲ when older content sits above the viewport, ▼ when the user has
        // scrolled up (content below). Each glyph is lit in the user colour when that direction is
        // available, dim otherwise: scrolling is the user's own action, not the speaker's, so it stays
        // outside the colour the header band spends on whoever holds the mic. The whole segment is
        // omitted unless at least one glyph is actionable, so a live, fully-visible conversation keeps
        // a clean header. BuildBody set these flags for this same frame (see BuildLayout's ordering).
        string scrollSegment = scrollHasAbove || scrollHasBelow
            ? $"   {(scrollHasAbove ? $"[{UserColor}]▲[/]" : "[grey50]▲[/]")}{(scrollHasBelow ? $"[{UserColor}]▼[/]" : "[grey50]▼[/]")}"
            : string.Empty;

        // Input-length counter: shown only while a text prompt is actually being typed — i.e. NOT
        // while awaiting a reply, NOT on the dead latch, NOT in quick-reply mode (no buffer there),
        // and only once at least one char is in the buffer. It's the prompt's own "dust gauge":
        // white → orange at ≥70% → red at ≥100% (reusing the dust palette), so a line creeping
        // toward the cap telegraphs the same unease as a depleting budget and the swallowed
        // keystrokes at the top read as "you hit the cap" rather than a glitch.
        string countSegment = string.Empty;
        if (!awaitingResponse && !conversationDead && !quickReplyActive && currentInput.Length >= 1)
        {
            string countColor =
                currentInput.Length >= maxInputLength ? DustCriticalColor :
                currentInput.Length * 10 >= maxInputLength * 7 ? DustLowColor :
                UserColor;
            countSegment = $"   [grey54]chars[/] [bold {countColor}]{currentInput.Length}/{maxInputLength}[/]";
        }

        // Markup uses [/] to close the tag — always run user-controlled strings through
        // Markup.Escape so a speaker name containing '[' can't break the layout.
        Markup content = new(
            $"[bold {speakerColor}]{Markup.Escape(currentSpeaker)}[/]   " +
            $"[grey54]conv[/] [bold {MorganaColor}]{Markup.Escape(shortId)}[/]" +
            dustSegment + scrollSegment + countSegment);

        return new Panel(Align.Center(content, VerticalAlignment.Middle))
        {
            Border = BoxBorder.Rounded,
            // The frame carries the same colour as the speaker name inside it, so who holds the mic
            // is legible from the whole header band rather than from one word in it.
            BorderStyle = Style.Parse(speakerColor),
            Header = new PanelHeader(""),
            Padding = new Padding(1, 0, 1, 0),
            Expand = true
        };
    }

    /// <summary>
    /// Renders the body cell as a flat stream of pre-wrapped rows: history rows (head
    /// rows naturally dropped) on top, sacred input row(s) inchiodate al fondo.
    /// </summary>
    /// <remarks>
    /// Model: every history message is converted to a list of single-line <see cref="Markup"/>
    /// rows via char-wrap at the current terminal width. The full concatenated stream is
    /// derived on every call from <see cref="history"/> (source of truth, append-only) and
    /// only its tail of <c>bodyRows</c> rows survives — older rows fall off the head
    /// regardless of whether the cut lands at a message boundary or splits a message
    /// open. The user prompt is then appended below, also pre-wrapped row-by-row so
    /// Spectre has no chance to word-wrap it differently than our budget accounting
    /// expects; the cursor row is therefore guaranteed to fit exactly where we put it.
    /// In the truly pathological case where the input alone exceeds the body cell, we
    /// drop input rows from the head (oldest typed prefix) rather than from the tail —
    /// the cursor row is the absolute floor of the sacred-prompt invariant.
    /// </remarks>
    private IRenderable BuildBody()
    {
        (int termWidth, int termHeight) = ReadViewport();
        int bodyHeight = Math.Max(0, termHeight - 3 /* header panel */);

        List<IRenderable> inputRows = BuildInputRows(termWidth);
        if (inputRows.Count > bodyHeight && bodyHeight > 0)
        {
            // Input alone overflows the body cell. Drop the head of the input row list
            // (oldest typed prefix) so the cursor row stays visible. This is the sacred-
            // prompt invariant degrading gracefully: the chevron may go, the cursor stays.
            inputRows = inputRows.GetRange(inputRows.Count - bodyHeight, bodyHeight);
        }

        // The scrollable content is the whole conversation stream: the history rows followed by the
        // live streaming pane. The input row(s) are NOT part of the stream: they stay pinned at the
        // bottom (the sacred prompt), so the content gets whatever height the input leaves free.
        RenderNewHistoryRows(termWidth);
        List<IRenderable> streamingRows = BuildStreamingRows(termWidth);
        int contentRowCount = historyRows.Count + streamingRows.Count;

        int contentHeight = Math.Max(0, bodyHeight - inputRows.Count);

        // Anchor the window. scrollOffset counts rows up from the bottom of the stream; clamp it
        // to the live content so a resize or a shorter conversation can't strand the viewport off
        // the end. Scrolling is only enabled at rest (see ReadKeysLoop), so during a turn the
        // offset is 0 and this pins to the bottom.
        int maxOffset = Math.Max(0, contentRowCount - contentHeight);
        scrollOffset = Math.Clamp(scrollOffset, 0, maxOffset);
        int windowEnd = contentRowCount - scrollOffset;
        int windowStart = Math.Max(0, windowEnd - contentHeight);

        // Light the header glyphs only when scrolling is actually actionable — don't tease ▲▼
        // mid-stream or in QR mode, where the keys do nothing (or mean something else).
        bool scrollable = !quickReplyActive && !awaitingResponse;
        scrollHasAbove = scrollable && windowStart > 0;
        scrollHasBelow = scrollable && scrollOffset > 0;

        List<IRenderable> rows = new(contentHeight + inputRows.Count);
        for (int i = windowStart; i < windowEnd; i++)
            rows.Add(i < historyRows.Count ? historyRows[i] : streamingRows[i - historyRows.Count]);
        rows.AddRange(inputRows);
        return new Rows(rows);
    }

    /// <summary>
    /// Renders <see cref="streamingDisplayed"/> through the markdown pipeline as a list of
    /// pre-wrapped single-row markups, with prose tinted in the specialised-agent color.
    /// Streaming is assumed to originate from agents (base Morgana turns ship as single
    /// messages, not chunked) so the pane uses <see cref="MorganaAgentColor"/> as the base
    /// without threading an agent-name field through the chunk wire shape. Returns an empty
    /// list when no character has been revealed yet — keeping the prompt area unchanged.
    /// </summary>
    /// <remarks>
    /// Markdown is rendered <em>live</em>, exactly like Cauldron's bubble (which runs
    /// <c>MarkdownRendererService.ToHtml</c> on every re-render, streaming included): the
    /// buffer is re-parsed each typewriter tick, so syntax resolves progressively as the
    /// closing tokens arrive and there is no grezzo→formatted "snap" when the turn commits
    /// to history. <see cref="MarkdownTerminalRenderService.Wrap"/> still guarantees one terminal
    /// row per <see cref="Markup"/>, so the budget accounting in <see cref="BuildBody"/> holds.
    /// </remarks>
    private List<IRenderable> BuildStreamingRows(int termWidth)
    {
        if (streamingDisplayed.Length == 0)
            return [];

        // No speaker prefix on the streaming pane (matches prior behaviour); no caching —
        // the buffer changes every tick and Markdig is cheap on these small payloads.
        return [.. markdownRenderer.RenderToRows(streamingDisplayed, MorganaAgentColor, null, termWidth)];
    }

    /// <summary>
    /// Renders <paramref name="message"/> to single-row markups: its text through
    /// <see cref="MarkdownTerminalRenderService"/> with the speaker colour as base prose colour and
    /// a bold <c>"Who: "</c> prefix on the first row, followed — when the message carries a
    /// <see cref="DisplayedMessage.Card"/> — by the card Spectrized through
    /// <see cref="RichCardTerminalRenderService"/> (separated by one blank row, mirroring
    /// Cauldron's text-bubble-then-card stacking).
    /// </summary>
    private List<Markup> RenderMessageRows(DisplayedMessage message, int termWidth)
    {
        List<Markup> rows = markdownRenderer.RenderToRows(message.Text, message.Color, $"{message.Who}: ", termWidth);
        if (message.Card is not null)
        {
            rows.Add(new Markup(string.Empty));
            rows.AddRange(richCardRenderer.RenderRichCard(message.Card, message.Color, termWidth));
        }
        return rows;
    }

    /// <summary>
    /// Brings <see cref="historyRows"/> up to date with <see cref="history"/> at <paramref name="termWidth"/>:
    /// only the messages committed since the previous frame are rendered, unless the width changed.
    /// Must be called under <see cref="renderLock"/>.
    /// </summary>
    private void RenderNewHistoryRows(int termWidth)
    {
        if (historyRowsWidth != termWidth)
        {
            historyRows.Clear();
            historyRowsMessageCount = 0;
            historyRowsWidth = termWidth;
        }

        for (; historyRowsMessageCount < history.Count; historyRowsMessageCount++)
            historyRows.AddRange(RenderMessageRows(history[historyRowsMessageCount], termWidth));
    }

    /// <summary>
    /// Builds the input/hint area as a list of pre-wrapped single-row markups.
    /// Two regimes match the two states <see cref="BuildBody"/>'s caller can be in:
    /// <c>awaitingResponse</c> (italic "speaker is thinking…" hint) and prompt mode
    /// (<c>›</c> chevron + buffer + blinking <c>_</c> cursor).
    /// </summary>
    /// <remarks>
    /// Pre-chunking at <paramref name="termWidth"/> sidesteps Spectre's word-wrap
    /// heuristics: each emitted Markup is at most <paramref name="termWidth"/> visible
    /// columns wide, so Spectre renders one row per Markup, no surprises. The row
    /// count returned here is therefore the exact number of body rows the prompt
    /// will occupy — a tight contract on which the history budget depends.
    /// </remarks>
    private List<IRenderable> BuildInputRows(int termWidth)
    {
        // Terminal state supersedes everything. Morgana's banner already told the user
        // the dust ran out; here we give the Grimoire-honest next step, because Morgana's
        // "start a new one to keep going" is not (yet) actionable inside this CLI —
        // the only way forward is to quit and relaunch the process.
        if (conversationDead)
            return ChunkStyledRows("✦ Conversation spent — press Esc to quit, then relaunch Grimoire to start fresh", termWidth, $"{ErrorColor} italic");

        // Quick replies own the prompt for this turn (set in CommitFinalMessage): render the
        // selectable options in place of the text input. The accent is the user colour because
        // this is the user's choice surface. Markup → IRenderable via the spread, as elsewhere.
        // Framed like the free-text prompt: picking an option is still the user's turn to act,
        // just via arrow keys instead of typing.
        if (quickReplyActive && activeQuickReplies is { Count: > 0 } replies)
            return FramePromptRows([.. quickReplyRenderer.RenderQuickReplies(replies, quickReplyIndex, UserColor, termWidth)], termWidth);

        if (awaitingResponse)
        {
            // While chunks are landing the streaming pane is the focal element — the
            // "is thinking…" hint would compete with it for the bottom-most row. Suppress
            // as soon as the typewriter has revealed at least one character.
            if (streamingDisplayed.Length > 0)
                return [];

            // Tinted in the speaker's own colour (primary purple for base Morgana, secondary pink
            // for a specialised agent) instead of a flat grey, so the hint already signals who's
            // about to answer before the first token of the reply arrives.
            string content = $"{currentSpeaker} is thinking…";
            return ChunkStyledRows(content, termWidth, $"{SpeakerColor(currentSpeaker)} italic");
        }

        // Visible layout, one cell per visible column: chevron, a space, then the input chars —
        // with the caret drawn ON the char it sits before (inverted block) so it stays visible
        // mid-line, or as a trailing blink '_' when it's at end-of-line. Each cell is exactly one
        // column wide, so packing termWidth cells per row keeps the exact-width contract the
        // history budget relies on, no matter where the caret lands.
        List<string> cells = new(currentInput.Length + 3)
        {
            $"[{UserColor}]›[/]",
            " "
        };
        for (int i = 0; i < currentInput.Length; i++)
        {
            string ch = Markup.Escape(currentInput[i].ToString());
            cells.Add(i == cursorPosition ? $"[blink {UserColor} invert]{ch}[/]" : ch);
        }
        if (cursorPosition >= currentInput.Length)
            cells.Add($"[blink {UserColor}]_[/]");

        List<IRenderable> rows = [];
        StringBuilder sb = new(capacity: termWidth + 32 /* slack for style tags */);
        int col = 0;
        foreach (string cell in cells)
        {
            if (col == termWidth)
            {
                rows.Add(new Markup(sb.ToString()));
                sb.Clear();
                col = 0;
            }
            sb.Append(cell);
            col++;
        }
        if (sb.Length > 0)
            rows.Add(new Markup(sb.ToString()));
        return FramePromptRows(rows, termWidth);
    }

    /// <summary>Sandwiches the row(s) where the user actually acts — free-text typing or the quick-reply picker — between two full-width rules in the same grey as the header panel's border, so that surface isn't marked only by the caret or the option highlight. Not used for the thinking hint or the dead-conversation notice: those are status, not something the user is doing right now.</summary>
    private static List<IRenderable> FramePromptRows(List<IRenderable> rows, int termWidth)
    {
        // Nothing to frame while the streaming pane owns the bottom row — bordering emptiness
        // would just steal two rows from history for no visual gain.
        if (rows.Count == 0)
            return rows;

        // Neutral grey keeps the prompt frame as plain chrome: the speaker colour is spoken for by
        // the header band, so the input area must not read as a second, competing accent.
        Markup border = new($"[grey50]{new string('─', Math.Max(1, termWidth))}[/]");
        return [border, .. rows, border];
    }

    /// <summary>Splits <paramref name="content"/> into <paramref name="termWidth"/>-wide chunks, each rendered as a single-row <see cref="Markup"/> wrapped in <paramref name="style"/>.</summary>
    private static List<IRenderable> ChunkStyledRows(string content, int termWidth, string style)
    {
        if (content.Length == 0)
            return [new Markup(string.Empty)];

        List<IRenderable> rows = new(capacity: (content.Length + termWidth - 1) / termWidth);
        int offset = 0;
        while (offset < content.Length)
        {
            int len = Math.Min(termWidth, content.Length - offset);
            string chunk = content.Substring(offset, len);
            rows.Add(new Markup($"[{style}]{Markup.Escape(chunk)}[/]"));
            offset += len;
        }
        return rows;
    }

    /// <summary>Maps a speaker name to its palette color: <c>You</c> → white, <c>Morgana</c> → purple, everything else (specialised agents) → pink.</summary>
    private static string SpeakerColor(string agentName)
    {
        if (agentName.Equals("You", StringComparison.OrdinalIgnoreCase))
            return UserColor;
        if (agentName.Equals("Morgana", StringComparison.OrdinalIgnoreCase))
            return MorganaColor;
        return MorganaAgentColor;
    }

    /// <summary>
    /// Row color for an inbound message: advisory warnings (rate-limit, low-budget —
    /// <c>MessageType="system_warning"</c>) render orange, error notices (dust exhausted,
    /// delivery error — <c>MessageType="error"</c>) render red, everything else keeps the
    /// speaker's palette color. Grimoire has no banner widget, so color is the only signal that
    /// separates a diagnostic line from an ordinary turn.
    /// </summary>
    private static string RowColor(ChannelMessage message, string speaker)
    {
        if (string.Equals(message.MessageType, "error", StringComparison.OrdinalIgnoreCase))
            return ErrorColor;
        if (string.Equals(message.MessageType, "system_warning", StringComparison.OrdinalIgnoreCase))
            return WarningColor;
        return SpeakerColor(speaker);
    }

    /// <summary>
    /// Mirrors Cauldron's ChatStateService.IsSpecializedAgent: a specialised agent
    /// announces itself as <c>Morgana (Intent)</c>, so the presence of parentheses
    /// is the discriminator between base Morgana and a domain agent.
    /// </summary>
    private static bool IsSpecializedAgent(string? agentName) =>
        agentName is not null && agentName.Contains('(') && agentName.Contains(')');

    /// <summary>
    /// A single history entry: speaker name, raw (markdown) text, the base Spectre colour token
    /// for the speaker and an optional <see cref="Card"/> rendered beneath the text.
    /// </summary>
    private sealed class DisplayedMessage(string who, string text, string color, RichCard? card = null)
    {
        public string Who { get; } = who;
        public string Text { get; } = text;
        public string Color { get; } = color;

        /// <summary>Optional rich card delivered with the message, Spectrized beneath the prose. Null for user echoes, system notices and agent-completion courtesy lines.</summary>
        public RichCard? Card { get; } = card;
    }

    /// <summary>
    /// Tagged union for items posted to <see cref="inbound"/>: full <see cref="ChannelMessage"/>s
    /// or incremental stream chunks. Lets a single drain loop preserve emit order across both
    /// webhook surfaces.
    /// </summary>
    private abstract record InboundEvent;

    /// <summary>Full <see cref="ChannelMessage"/> arrived on <c>/morgana-hook</c>.</summary>
    private sealed record MessageEvent(ChannelMessage Message) : InboundEvent;

    /// <summary>Incremental delta arrived on <c>/morgana-hook/chunk</c>.</summary>
    private sealed record ChunkEvent(string ChunkText) : InboundEvent;
}