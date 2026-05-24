using System.Text;
using System.Threading.Channels;
using Grimoire.Interfaces;
using Grimoire.Messages.Contracts;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Grimoire.Services;

/// <summary>
/// Terminal UI for Grimoire built on Spectre.Console's <see cref="LiveDisplay"/>: a sticky
/// header pinned at the top shows the conversation id and the current speaker
/// (<c>Morgana</c> → magenta1; <c>Morgana (Agent)</c> → hotpink), a scrolling body
/// prints the chat history colored by speaker (the user is azure / skyblue1), and the
/// bottom-most line hosts the input buffer with a blinking cursor. Incoming webhook
/// messages are drained from a channel and trigger a refresh; user keystrokes are
/// captured via <see cref="Console.ReadKey(bool)"/> on a pool thread.
/// </summary>
/// <remarks>
/// Input is gated across Morgana's turn: once the user commits a line (Enter), the
/// bottom line flips to a <c>"{speaker} is thinking…"</c> hint and all subsequent
/// keystrokes — except <see cref="ConsoleKey.Escape"/>, which always exits — are
/// silently swallowed until the next webhook delivery lands and flips the gate back.
/// The initial state is also gated so nothing can be typed while waiting for the
/// presentation message, matching Cauldron's typing-indicator semantics. Send
/// failures release the gate so the user can retry.
/// </remarks>
public sealed class ConsoleUiService
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

    /// <summary>Fallback for <c>Grimoire:AgentExitMessage</c> when the setting is absent.</summary>
    private const string DefaultAgentExitMessage = "{0} has completed its spell. I'm back to you!";

    /// <summary>
    /// Source of truth: every message ever displayed, chronologically ordered, append-only.
    /// <see cref="BuildBody"/> derives the viewport on the fly by wrap-rendering the whole
    /// list into a flat row stream and taking the last <c>bodyRows</c> rows — head rows are
    /// dropped naturally as the stream grows or the terminal shrinks, the tail (warm rows)
    /// is always preserved, and the sacred user prompt is rendered separately under the
    /// stream and is never sacrificed.
    /// </summary>
    private readonly List<DisplayedMessage> history = [];

    /// <summary>
    /// Single FIFO queue for everything Morgana pushes over the webhook surface — both full
    /// <see cref="ChannelMessage"/>s (<c>/morgana-hook</c>) and incremental stream chunks
    /// (<c>/morgana-hook/chunk</c>) land here as <see cref="InboundEvent"/>s. Unifying the two
    /// streams in one channel preserves the producer's emit order all the way to the drain
    /// loop: a final message can never overtake a trailing chunk and clear the streaming
    /// buffer before that chunk has been merged into it.
    /// </summary>
    private readonly Channel<InboundEvent> inbound = Channel.CreateUnbounded<InboundEvent>();

    /// <summary>Format string for the courtesy line appended when a specialised agent completes.</summary>
    private readonly string agentExitTemplate;

    /// <summary>Buffer holding keystrokes not yet committed with <see cref="ConsoleKey.Enter"/>.</summary>
    private string currentInput = string.Empty;

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
    /// Set true when the final <see cref="ChannelMessage"/> for the turn arrives while the
    /// typewriter is still draining <see cref="streamingPending"/>. Tells the timer "no more
    /// chunks are coming — drain what you have, then commit". Reset on commit.
    /// </summary>
    private bool streamingComplete;

    /// <summary>
    /// The final <see cref="ChannelMessage"/> deferred until the typewriter finishes revealing the
    /// buffered text. Captured by <see cref="HandleInboundMessage"/> when the buffer is non-empty,
    /// consumed by <see cref="TypewriterTick"/> once the buffer drains.
    /// </summary>
    private ChannelMessage? pendingFinalMessage;

    /// <summary>Active typewriter timer, or null when no streaming session is in flight.</summary>
    private Timer? typewriterTimer;

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

    /// <summary>Platform-specific terminal-resize notifier; subscribed in <see cref="RunAsync"/> so the viewport anchor follows live window resizes without per-frame polling.</summary>
    private readonly IViewportResizeWatcher viewportResizeWatcher;

    /// <summary>Serializes mutations of <see cref="history"/>, <see cref="currentInput"/>, <see cref="currentSpeaker"/> and the paired <see cref="LiveDisplayContext.UpdateTarget"/>/<see cref="LiveDisplayContext.Refresh"/> calls. The resize callback runs on its own thread (SIGWINCH handler / polling task) and would otherwise race with <see cref="ReadKeysLoop"/> and <see cref="DrainIncomingLoop"/> over the shared list. Uses <see cref="System.Threading.Lock"/> (.NET 9+) instead of <c>object</c> so the compiler emits the optimised primitive and rejects misuse (e.g. passing the lock as an <c>object</c>).</summary>
    private readonly Lock renderLock = new();

    /// <summary>Reads the <c>Grimoire:AgentExitMessage</c> template, falling back to <see cref="DefaultAgentExitMessage"/>, the typewriter cadence settings, and captures the injected resize watcher.</summary>
    public ConsoleUiService(IConfiguration configuration, IViewportResizeWatcher viewportResizeWatcher)
    {
        agentExitTemplate = configuration["Grimoire:AgentExitMessage"] ?? DefaultAgentExitMessage;
        this.viewportResizeWatcher = viewportResizeWatcher;

        // Typewriter cadence — mirror of Cauldron's Cauldron:StreamingResponse:Typewriter* keys.
        // Non-positive values fall back to the Cauldron defaults (15 ms, 1 char/tick) so a
        // misconfiguration can't freeze the reveal or starve the buffer.
        streamingTickMilliseconds = configuration.GetValue<int?>("Grimoire:StreamingResponse:TypewriterTickMilliseconds") ?? 15;
        if (streamingTickMilliseconds <= 0)
            streamingTickMilliseconds = 15;
        streamingTickChars = configuration.GetValue<int?>("Grimoire:StreamingResponse:TypewriterTickChars") ?? 1;
        if (streamingTickChars <= 0)
            streamingTickChars = 1;
    }

    /// <summary>Called by <see cref="WebhookReceiverService"/> when Morgana delivers a message.</summary>
    public void EnqueueIncoming(ChannelMessage message) => inbound.Writer.TryWrite(new MessageEvent(message));

    /// <summary>Called by <see cref="WebhookReceiverService"/> when Morgana delivers a streaming chunk.</summary>
    public void EnqueueChunk(string chunkText) => inbound.Writer.TryWrite(new ChunkEvent(chunkText));

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
            EnsureTypewriterStarted(ctx);
        }
    }

    /// <summary>Boots <see cref="typewriterTimer"/> if it isn't already running. Idempotent; safe to call on every chunk.</summary>
    private void EnsureTypewriterStarted(LiveDisplayContext ctx)
    {
        if (typewriterTimer is not null)
            return;

        // Fire immediately (dueTime=0) and then every streamingTickMilliseconds. The first tick
        // takes renderLock again — the lock is reentrant-safe via System.Threading.Lock semantics
        // is NOT true; Lock is NOT reentrant, so the tick MUST run on a different thread.
        // Timer callbacks always run on the threadpool, never on the calling thread, so we're safe.
        typewriterTimer = new Timer(_ => TypewriterTick(ctx), null, 0, streamingTickMilliseconds);
    }

    /// <summary>
    /// Reveals up to <see cref="streamingTickChars"/> characters from <see cref="streamingPending"/>
    /// into <see cref="streamingDisplayed"/> and refreshes the UI. When the buffer drains AND the
    /// final message has already arrived (<see cref="streamingComplete"/>), commits the deferred
    /// <see cref="pendingFinalMessage"/> into history and tears the session down.
    /// </summary>
    private void TypewriterTick(LiveDisplayContext ctx)
    {
        ChannelMessage? finalToCommit = null;
        lock (renderLock)
        {
            if (streamingPending.Length > 0)
            {
                int charsToTake = Math.Min(streamingTickChars, streamingPending.Length);
                streamingDisplayed += streamingPending[..charsToTake];
                streamingPending = streamingPending[charsToTake..];
                ctx.UpdateTarget(BuildLayout());
                ctx.Refresh();
                return;
            }

            // Buffer is empty. If the final has landed, capture it for commit below; otherwise
            // idle here — more chunks may still be on the way (the tick will re-check next time).
            if (!streamingComplete)
                return;

            finalToCommit = pendingFinalMessage;
            pendingFinalMessage = null;
            streamingComplete = false;
            streamingDisplayed = string.Empty;
            StopTypewriter();
        }

        // Commit outside the lock — CommitFinalMessage takes the lock itself and we don't want
        // to acquire it twice (System.Threading.Lock is not reentrant).
        if (finalToCommit is not null)
            CommitFinalMessage(ctx, finalToCommit);
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
            if (typewriterTimer is not null && (streamingPending.Length > 0 || streamingDisplayed.Length > 0))
            {
                // Typewriter still revealing: stash the final and let the timer commit when
                // streamingPending drains. The deferred path keeps the typewriter rhythm intact
                // and avoids the "snap to full text" jump Cauldron exhibits on FinalizeStreaming.
                pendingFinalMessage = message;
                streamingComplete = true;
                return exitRequested;
            }

            // No active typewriter — go straight to history.
        }

        CommitFinalMessage(ctx, message);
        return exitRequested;
    }

    /// <summary>
    /// Commits a final <see cref="ChannelMessage"/> to history, updates the dust gauge / header /
    /// dead-state latch, and refreshes the live view. Invoked either inline by
    /// <see cref="HandleInboundMessage"/> (no streaming) or by <see cref="TypewriterTick"/> once
    /// the buffer drains.
    /// </summary>
    private void CommitFinalMessage(LiveDisplayContext ctx, ChannelMessage message)
    {
        // Attribute the row to whoever authored it: a specialised agent keeps its
        // own colour even on the farewell line that carries AgentCompleted=true.
        string messageSpeaker = string.IsNullOrWhiteSpace(message.AgentName) ? "Morgana" : message.AgentName;

        lock (renderLock)
        {

            history.Add(new DisplayedMessage(messageSpeaker, message.Text, RowColor(message, messageSpeaker), message.RichCard));

            // On agent completion append a base-Morgana courtesy line — same pattern
            // as Cauldron's ChatStateService.AddCompletionMessageIfNeeded.
            if (message.AgentCompleted && IsSpecializedAgent(message.AgentName))
            {
                string completion = string.Format(agentExitTemplate, message.AgentName);
                history.Add(new DisplayedMessage("Morgana", completion, MorganaColor));
            }

            // Revert the sticky header to Morgana on completion so the next user
            // turn doesn't render under the outgoing agent's colour.
            currentSpeaker = message.AgentCompleted || string.IsNullOrWhiteSpace(message.AgentName)
                ? "Morgana"
                : message.AgentName;

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
            // line above stays as Morgana's canonical word, and BuildInputRows
            // overrides the prompt with a Grimoire-honest "quit and relaunch" hint.
            if (string.Equals(message.ErrorReason, "dust_budget_exhausted", StringComparison.Ordinal))
            {
                conversationDead = true;
                currentInput = string.Empty; // discard any half-typed doomed line
            }

            // Release the input gate: ReadKeysLoop was swallowing keystrokes until
            // this first webhook delivery landed. (No-op once conversationDead:
            // ReadKeysLoop keeps swallowing on the dead latch regardless.)
            awaitingResponse = false;
            ctx.UpdateTarget(BuildLayout());
            ctx.Refresh();
        }
    }

    /// <summary>Polls <see cref="Console.KeyAvailable"/> every 25 ms and dispatches keys: Enter commits (or exits on <c>/quit</c>), Backspace edits, Esc exits, printable chars append to the buffer.</summary>
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
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                exitRequested = true;
                inbound.Writer.TryComplete();
                return;
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
                        ctx.UpdateTarget(BuildLayout());
                        ctx.Refresh();
                    }

                    try
                    {
                        await onSend(toSend);
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
                        if (currentInput.Length > 0)
                        {
                            currentInput = currentInput[..^1];
                            ctx.UpdateTarget(BuildLayout());
                            ctx.Refresh();
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
                            currentInput += key.KeyChar;
                            ctx.UpdateTarget(BuildLayout());
                            ctx.Refresh();
                        }
                    }
                    break;
            }
        }
    }

    /// <summary>Builds the two-row Spectre layout (fixed 3-row header + flex body).</summary>
    private Layout BuildLayout()
    {
        Layout root = new Layout("root")
            .SplitRows(
                new Layout("header").Size(3),
                new Layout("body").Ratio(1));

        root["header"].Update(BuildHeader());
        root["body"].Update(BuildBody());
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

        // Markup uses [/] to close the tag — always run user-controlled strings through
        // Markup.Escape so a speaker name containing '[' can't break the layout.
        Markup content = new(
            $"[bold {speakerColor}]{Markup.Escape(currentSpeaker)}[/]   " +
            $"[grey54]conv[/] [bold {MorganaColor}]{Markup.Escape(shortId)}[/]" +
            dustSegment);

        return new Panel(Align.Center(content, VerticalAlignment.Middle))
        {
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(Color.Grey50),
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
        int termWidth = Math.Max(1, Console.WindowWidth);
        int bodyHeight = Math.Max(0, Console.WindowHeight - 3 /* header panel */);

        List<IRenderable> inputRows = BuildInputRows(termWidth);
        if (inputRows.Count > bodyHeight && bodyHeight > 0)
        {
            // Input alone overflows the body cell. Drop the head of the input row list
            // (oldest typed prefix) so the cursor row stays visible. This is the sacred-
            // prompt invariant degrading gracefully: the chevron may go, the cursor stays.
            inputRows = inputRows.GetRange(inputRows.Count - bodyHeight, bodyHeight);
        }

        List<IRenderable> streamingRows = BuildStreamingRows(termWidth);
        int availableForStreaming = Math.Max(0, bodyHeight - inputRows.Count);
        if (streamingRows.Count > availableForStreaming)
        {
            // Streaming buffer outgrew the room left under history. Drop head rows (oldest
            // delta) so the tail — the latest tokens the user is reading right now — stays
            // visible. History gets zero budget in this case; that's fine, the streaming
            // pane is the focal element of the in-progress turn.
            streamingRows = streamingRows.GetRange(streamingRows.Count - availableForStreaming, availableForStreaming);
        }

        int historyBudget = Math.Max(0, bodyHeight - inputRows.Count - streamingRows.Count);
        List<IRenderable> rows = new(historyBudget + streamingRows.Count + inputRows.Count);
        AppendHistoryTail(rows, termWidth, historyBudget);
        rows.AddRange(streamingRows);
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
        // the buffer changes every tick, and Markdig is cheap on these small payloads.
        return [.. MarkdownTerminalRenderService.RenderToRows(streamingDisplayed, MorganaAgentColor, null, termWidth)];
    }

    /// <summary>
    /// Walks <see cref="history"/> from tail to head, summing wrap-row counts until it
    /// covers <paramref name="budget"/> rows, then renders forward from that point so
    /// the produced rows are exactly <paramref name="budget"/>-tall. If the head-side
    /// message is partially visible, its leading wrap rows are skipped — that's the
    /// sacrifice-head-never-tail invariant applied at row granularity.
    /// </summary>
    private void AppendHistoryTail(List<IRenderable> output, int termWidth, int budget)
    {
        if (budget == 0 || history.Count == 0)
            return;

        int firstMessageIdx = 0;
        int skipFromFirst = 0;
        int covered = 0;
        for (int i = history.Count - 1; i >= 0; i--)
        {
            int wrapRows = MessageWrapRowCount(history[i], termWidth);
            if (covered + wrapRows >= budget)
            {
                firstMessageIdx = i;
                skipFromFirst = wrapRows - (budget - covered);
                break;
            }
            covered += wrapRows;
            firstMessageIdx = i;
        }

        for (int i = firstMessageIdx; i < history.Count; i++)
        {
            int skip = i == firstMessageIdx ? skipFromFirst : 0;
            AppendMessageRows(output, history[i], termWidth, skip);
        }
    }

    /// <summary>
    /// Number of terminal rows <paramref name="message"/> spans at <paramref name="termWidth"/>,
    /// computed by rendering it through the markdown pipeline (cached). Always ≥ 1.
    /// </summary>
    private static int MessageWrapRowCount(DisplayedMessage message, int termWidth)
        => RenderMessageRows(message, termWidth).Count;

    /// <summary>
    /// Emits the cached markdown-rendered rows of <paramref name="message"/>, skipping the
    /// first <paramref name="skipRows"/> head rows (the sacrifice-head-never-tail invariant
    /// applied at row granularity).
    /// </summary>
    private static void AppendMessageRows(List<IRenderable> output, DisplayedMessage message, int termWidth, int skipRows)
    {
        List<Markup> rows = RenderMessageRows(message, termWidth);
        for (int i = Math.Max(0, skipRows); i < rows.Count; i++)
            output.Add(rows[i]);
    }

    /// <summary>
    /// Renders <paramref name="message"/> to single-row markups: its text through
    /// <see cref="MarkdownTerminalRenderService"/> with the speaker colour as base prose colour and
    /// a bold <c>"Who: "</c> prefix on the first row, followed — when the message carries a
    /// <see cref="DisplayedMessage.Card"/> — by the card Spectrized through
    /// <see cref="RichCardTerminalRenderService"/> (separated by one blank row, mirroring
    /// Cauldron's text-bubble-then-card stacking). The result is memoised on the message keyed by
    /// <paramref name="termWidth"/> so the typewriter's 15 ms ticks — which re-render the whole
    /// body — don't re-parse markdown for every history entry on every frame. The cache
    /// invalidates automatically when the terminal width changes (resize).
    /// </summary>
    /// <remarks>Mutates <paramref name="message"/>'s cache fields; always invoked under <see cref="renderLock"/> via <see cref="BuildBody"/>.</remarks>
    private static List<Markup> RenderMessageRows(DisplayedMessage message, int termWidth)
    {
        if (message.CachedWidth == termWidth && message.CachedRows is not null)
            return message.CachedRows;

        List<Markup> rows = MarkdownTerminalRenderService.RenderToRows(message.Text, message.Color, $"{message.Who}: ", termWidth);
        if (message.Card is not null)
        {
            rows.Add(new Markup(string.Empty));
            rows.AddRange(RichCardTerminalRenderService.RenderRichCard(message.Card, message.Color, termWidth));
        }
        message.CachedRows = rows;
        message.CachedWidth = termWidth;
        return rows;
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

        if (awaitingResponse)
        {
            // While chunks are landing the streaming pane is the focal element — the
            // "is thinking…" hint would compete with it for the bottom-most row. Suppress
            // as soon as the typewriter has revealed at least one character.
            if (streamingDisplayed.Length > 0)
                return [];

            string content = $"{currentSpeaker} is thinking…";
            return ChunkStyledRows(content, termWidth, "grey54 italic");
        }

        // Visible layout: position 0 = chevron, position 1 = space, positions 2..N+1 =
        // currentInput chars (N = currentInput.Length), position N+2 = cursor. Build the
        // markup row-by-row, applying the chevron / cursor style tags at their exact
        // positions so the cursor stays at the very end regardless of where the wrap
        // boundaries land.
        int totalLen = currentInput.Length + 3;
        int cursorPos = totalLen - 1;
        List<IRenderable> rows = [];
        int rowStart = 0;
        StringBuilder sb = new(capacity: termWidth + 32 /* slack for style tags */);
        while (rowStart < totalLen)
        {
            int rowEnd = Math.Min(rowStart + termWidth, totalLen);
            sb.Clear();
            for (int p = rowStart; p < rowEnd; p++)
            {
                if (p == 0)
                    sb.Append($"[{UserColor}]›[/]");
                else if (p == 1)
                    sb.Append(' ');
                else if (p == cursorPos)
                    sb.Append($"[blink {UserColor}]_[/]");
                else
                    sb.Append(Markup.Escape(currentInput[p - 2].ToString()));
            }
            rows.Add(new Markup(sb.ToString()));
            rowStart = rowEnd;
        }
        return rows;
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

    /// <summary>Maps a speaker name to its palette color: <c>You</c> → skyblue1, <c>Morgana</c> → magenta1, everything else → hotpink.</summary>
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
    /// for the speaker, and an optional <see cref="Card"/> rendered beneath the text. Carries a
    /// per-width memo of the rendered rows so the typewriter's per-tick full-body redraw doesn't
    /// re-parse markdown/cards for stable history on every frame — see <see cref="RenderMessageRows"/>.
    /// A class (not a record) because the cache fields are mutated in place after construction.
    /// </summary>
    private sealed class DisplayedMessage(string who, string text, string color, RichCard? card = null)
    {
        public string Who { get; } = who;
        public string Text { get; } = text;
        public string Color { get; } = color;

        /// <summary>Optional rich card delivered with the message, Spectrized beneath the prose. Null for user echoes, system notices and agent-completion courtesy lines.</summary>
        public RichCard? Card { get; } = card;

        /// <summary>Terminal width the cached rows were wrapped at, or -1 when never rendered.</summary>
        public int CachedWidth { get; set; } = -1;

        /// <summary>Memoised rendered rows valid for <see cref="CachedWidth"/>, or null when stale.</summary>
        public List<Markup>? CachedRows { get; set; }
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