using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Rune.Interfaces;
using Morgana.Contracts;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Rune.Services;

/// <summary>
/// Terminal UI for Rune built on Spectre.Console's <see cref="LiveDisplay"/>: a sticky
/// header pinned at the top shows the conversation id and the current speaker
/// (<c>Morgana</c> → emerald green; <c>Morgana (Agent)</c> → light green), a scrolling body
/// prints the chat history colored by speaker (the user is white), and the
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
    /// <summary>Color for base Morgana turns: Emerald green.</summary>
    private const string MorganaColor = "#10b981";

    /// <summary>Color for specialised agent turns: Light green.</summary>
    private const string MorganaAgentColor = "#6ee7b7";

    /// <summary>Color for the user's own input and committed lines.</summary>
    private const string UserColor = "white";

    /// <summary>Color for the magic-dust gauge in the sticky header.
    /// Matches MorganaColor.</summary>
    private const string DustColor = "#10b981";

    /// <summary>Gauge color when remaining ≤ 30% — Cauldron's <c>.dust-meter.low</c> amber.</summary>
    private const string DustLowColor = "#f59e0b";

    /// <summary>Gauge color when remaining ≤ 10% — Cauldron's <c>.dust-meter.critical</c> red.</summary>
    private const string DustCriticalColor = "#ef4444";

    /// <summary>Color for advisory warnings (rate-limit / low-budget): orange.</summary>
    private const string WarningColor = "orange1";

    /// <summary>Color for error notices (dust exhausted / delivery error): red.</summary>
    private const string ErrorColor = "red";

    /// <summary>Fallback for <c>Rune:AgentExitMessage</c> when the setting is absent.</summary>
    private const string DefaultAgentExitMessage = "{0} has completed its spell. I'm back to you!";

    /// <summary>
    /// Matches a run of three or more consecutive newlines (i.e. two or more blank lines).
    /// Rune renders Morgana's replies verbatim — unlike Grimoire it has no Markdig pipeline to
    /// normalize whitespace — so the doubled blank lines Morgana emits when formatting for rich
    /// channels would each count as a phantom row in <see cref="RenderMessageRows"/> and trip the
    /// scroll indicator on a conversation that actually fits. Collapsed to a single blank line,
    /// mirroring how a markdown renderer treats a paragraph break.
    /// </summary>
    private static readonly Regex BlankRunRegex = new(@"\n{3,}", RegexOptions.Compiled);

    /// <summary>
    /// Source of truth: every message ever displayed, chronologically ordered, append-only.
    /// <see cref="BuildBody"/> derives the viewport on the fly by wrap-rendering the whole
    /// list into a flat row stream and taking the last <c>bodyRows</c> rows — head rows are
    /// dropped naturally as the stream grows or the terminal shrinks, the tail (warm rows)
    /// is always preserved, and the sacred user prompt is rendered separately under the
    /// stream and is never sacrificed.
    /// </summary>
    private readonly List<DisplayedMessage> history = [];

    /// <summary>Thread-safe queue of messages posted by <see cref="WebhookReceiverService"/> awaiting render.</summary>
    private readonly Channel<ChannelMessage> incoming = Channel.CreateUnbounded<ChannelMessage>();

    /// <summary>Format string for the courtesy line appended when a specialised agent completes.</summary>
    private readonly string agentExitTemplate;

    /// <summary>Buffer holding keystrokes not yet committed with <see cref="ConsoleKey.Enter"/>.</summary>
    private string currentInput = string.Empty;

    /// <summary>
    /// Insertion caret as a <see cref="currentInput"/> char (UTF-16 code-unit) index, kept on a rune
    /// boundary so it never splits a surrogate pair (matching the rune-aware prompt rendering).
    /// Invariant <c>0 ≤ cursorPosition ≤ currentInput.Length</c>. Left/Right walk it; typing inserts and
    /// Backspace/Delete remove at this point, so a line can be fixed in place rather than only chopped at
    /// the tail. Reset to 0 whenever <see cref="currentInput"/> is cleared. Mutated only under <see cref="renderLock"/>.
    /// </summary>
    private int cursorPosition;

    /// <summary>
    /// Hard cap on the prompt length in UTF-16 chars. Typing past it is swallowed (←/→/Backspace/Delete
    /// stay live, so the line can still be edited down); the header counter makes the stop self-explanatory.
    /// From <c>Rune:MaxInputLength</c>, default 500. Also keeps the prompt short enough that it never
    /// out-grows the body cell on a normal terminal, so the caret can't scroll out of the kept tail.
    /// </summary>
    private readonly int maxInputLength;

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

    /// <summary>When true, keystrokes (except Esc) are swallowed and the input line shows a thinking hint. Starts true: Rune always waits for Morgana's presentation before the first user turn.</summary>
    private volatile bool awaitingResponse = true;

    /// <summary>
    /// Latched true when Morgana delivers the terminal dust-exhaustion notice
    /// (<c>ErrorReason == "dust_budget_exhausted"</c>). The conversation is spent and
    /// — unlike Cauldron — Rune cannot start a fresh one in-process, so this is a
    /// one-way door: input stays locked (only Esc works) and the prompt is replaced
    /// by an honest "quit and relaunch" hint. Never cleared. Mutated only under
    /// <see cref="renderLock"/>; volatile so <see cref="ReadKeysLoop"/> sees it
    /// without taking the lock on every polled keystroke.
    /// </summary>
    private volatile bool conversationDead;

    /// <summary>
    /// Scrollback offset in rows: how far above the live bottom the viewport is anchored.
    /// 0 = pinned to the newest content (the default live view). Only ever non-zero while the
    /// conversation is at rest — <see cref="ReadKeysLoop"/> gates scrolling on
    /// <c>!awaitingResponse</c>, so the window never moves under an in-flight turn — and it is
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

    /// <summary>Serializes mutations of <see cref="history"/>, <see cref="currentInput"/>, <see cref="currentSpeaker"/> and the paired <see cref="LiveDisplayContext.UpdateTarget"/>/<see cref="LiveDisplayContext.Refresh"/> calls. The resize callback runs on its own thread (SIGWINCH handler / polling task) and would otherwise race with <see cref="ReadKeysLoop"/> and <see cref="DrainIncomingLoop"/> over the shared list. Uses <see cref="System.Threading.Lock"/> (.NET 9+) instead of <c>object</c> so the compiler emits the optimised primitive and rejects misuse (e.g. passing the lock as an <c>object</c>).</summary>
    private readonly Lock renderLock = new();

    /// <summary>Reads the <c>Rune:AgentExitMessage</c> template, falling back to <see cref="DefaultAgentExitMessage"/>, and captures the injected resize watcher.</summary>
    public ConsoleUiService(IConfiguration configuration, IViewportResizeWatcher viewportResizeWatcher)
    {
        agentExitTemplate = configuration["Rune:AgentExitMessage"] ?? DefaultAgentExitMessage;
        this.viewportResizeWatcher = viewportResizeWatcher;

        // Non-positive (or absent) falls back to 500 so a misconfiguration can't lock the prompt shut.
        maxInputLength = configuration.GetValue<int?>("Rune:MaxInputLength") ?? 500;
        if (maxInputLength <= 0)
            maxInputLength = 500;
    }

    /// <summary>Called by <see cref="WebhookReceiverService"/> when Morgana delivers a message.</summary>
    public void EnqueueIncoming(ChannelMessage message) => incoming.Writer.TryWrite(message);

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
                Task drainLoop = Task.Run(() => DrainIncomingLoop(ctx, cancellationToken), cancellationToken);
                await Task.WhenAny(readLoop, drainLoop);
            });
    }

    /// <summary>Consumes the <see cref="incoming"/> channel, appends each message to the history (plus a courtesy line on agent completion) and refreshes the live view.</summary>
    private async Task DrainIncomingLoop(LiveDisplayContext ctx, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (ChannelMessage message in incoming.Reader.ReadAllAsync(cancellationToken))
            {
                // Attribute the row to whoever authored it: a specialised agent keeps its
                // own colour even on the farewell line that carries AgentCompleted=true.
                string messageSpeaker = string.IsNullOrWhiteSpace(message.AgentName) ? "Morgana" : message.AgentName;

                lock (renderLock)
                {
                    history.Add(new DisplayedMessage(messageSpeaker, message.Text, RowColor(message, messageSpeaker)));

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
                    // post-adaptation level — for a poor channel that just spent extra dust
                    // degrading the answer, the second value is lower and we WANT the gauge
                    // to snap to it. The earlier "double-render" was redundant identical
                    // repaints; now the two updates are intentionally distinct (pre → post)
                    // and Spectre's diffing makes an unchanged segment an invisible no-op.
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
                    // one to keep going" — true for Cauldron, NOT for Rune, which has no
                    // in-process restart. So latch a one-way dead state: the red banner
                    // line above stays as Morgana's canonical word, and BuildInputRows
                    // overrides the prompt with a Rune-honest "quit and relaunch" hint.
                    if (string.Equals(message.ErrorReason, "dust_budget_exhausted", StringComparison.Ordinal))
                    {
                        conversationDead = true;
                        currentInput = string.Empty; // discard any half-typed doomed line
                        cursorPosition = 0;
                    }

                    // Release the input gate: ReadKeysLoop was swallowing keystrokes until
                    // this first webhook delivery landed. (No-op once conversationDead:
                    // ReadKeysLoop keeps swallowing on the dead latch regardless.)
                    awaitingResponse = false;
                    ctx.UpdateTarget(BuildLayout());
                    ctx.Refresh();
                }

                // Honour an exit requested mid-turn only after painting the last frame,
                // so the user sees Morgana's final reply before the UI tears down.
                if (exitRequested)
                    return;
            }
        }
        catch (OperationCanceledException)
        {
            // Cancellation is the normal exit path — swallow.
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
                incoming.Writer.TryComplete();
                return;
            }

            // Scrollback: review the finished conversation. Enabled only at rest — while a turn is
            // in flight (awaitingResponse) the keys are ignored, so the window never moves under an
            // incoming reply. Handled before the swallow gate below so it still works once the
            // conversation is dust-dead (awaitingResponse is false there) for re-reading.
            if (!awaitingResponse && TryScrollDelta(key.Key, out int scrollDelta))
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
                        incoming.Writer.TryComplete();
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
                        // Delete the rune to the LEFT of the caret (whole surrogate pair) and step the
                        // caret back over it — not the tail: the two only coincide at end-of-line.
                        if (cursorPosition > 0)
                        {
                            int n = RuneLengthBefore(cursorPosition);
                            currentInput = currentInput.Remove(cursorPosition - n, n);
                            cursorPosition -= n;
                            ctx.UpdateTarget(BuildLayout());
                            ctx.Refresh();
                        }
                    }
                    break;
                case ConsoleKey.Delete:
                    lock (renderLock)
                    {
                        // Forward delete: remove the rune UNDER the caret, leaving the caret put.
                        if (cursorPosition < currentInput.Length)
                        {
                            int n = RuneLengthAt(cursorPosition);
                            currentInput = currentInput.Remove(cursorPosition, n);
                            ctx.UpdateTarget(BuildLayout());
                            ctx.Refresh();
                        }
                    }
                    break;
                case ConsoleKey.LeftArrow:
                    lock (renderLock)
                    {
                        if (cursorPosition > 0)
                        {
                            cursorPosition -= RuneLengthBefore(cursorPosition);
                            ctx.UpdateTarget(BuildLayout());
                            ctx.Refresh();
                        }
                    }
                    break;
                case ConsoleKey.RightArrow:
                    lock (renderLock)
                    {
                        if (cursorPosition < currentInput.Length)
                        {
                            cursorPosition += RuneLengthAt(cursorPosition);
                            ctx.UpdateTarget(BuildLayout());
                            ctx.Refresh();
                        }
                    }
                    break;
                case ConsoleKey.Escape:
                    // Unconditional exit — completes the channel so DrainIncomingLoop
                    // unblocks out of its await foreach and RunAsync can return.
                    exitRequested = true;
                    incoming.Writer.TryComplete();
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
                                ctx.UpdateTarget(BuildLayout());
                                ctx.Refresh();
                            }
                        }
                    }
                    break;
            }
        }
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
        // available, dim otherwise; the whole segment is omitted unless at least one is actionable
        // so a live, fully-visible conversation keeps a clean header. BuildBody set these flags for
        // this same frame (see BuildLayout's ordering), and only when scrolling is enabled.
        string scrollSegment = scrollHasAbove || scrollHasBelow
            ? $"   {(scrollHasAbove ? $"[{UserColor}]▲[/]" : "[grey50]▲[/]")}{(scrollHasBelow ? $"[{UserColor}]▼[/]" : "[grey50]▼[/]")}"
            : string.Empty;

        // Input-length counter: shown only while a text prompt is actually being typed — i.e. NOT
        // while awaiting a reply and NOT on the dead latch, and only once at least one char is in
        // the buffer (Rune has no quick-reply mode, so there's no QR case to exclude). It's the
        // prompt's own "dust gauge": white → orange at ≥70% → red at ≥100% (reusing the dust
        // palette), so a line creeping toward the cap telegraphs the same unease as a depleting
        // budget, and the swallowed keystrokes at the top read as "you hit the cap", not a glitch.
        string countSegment = string.Empty;
        if (!awaitingResponse && !conversationDead && currentInput.Length >= 1)
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

        // Materialise the whole conversation as single rows, then take a window of it. The input
        // row(s) are NOT part of the stream: they stay pinned at the bottom (the sacred prompt),
        // so the history gets whatever height the input leaves free.
        List<IRenderable> contentRows = [];
        foreach (DisplayedMessage message in history)
            contentRows.AddRange(RenderMessageRows(message, termWidth));

        int contentHeight = Math.Max(0, bodyHeight - inputRows.Count);

        // Anchor the window. scrollOffset counts rows up from the bottom; clamp it to the live
        // content so a resize or a shorter conversation can't strand the viewport off the end.
        // Scrolling is only enabled at rest (see ReadKeysLoop), so during a turn the offset is 0
        // and this pins to the bottom — the previous live behaviour, unchanged.
        int maxOffset = Math.Max(0, contentRows.Count - contentHeight);
        scrollOffset = Math.Clamp(scrollOffset, 0, maxOffset);
        int windowEnd = contentRows.Count - scrollOffset;
        int windowStart = Math.Max(0, windowEnd - contentHeight);

        // Light the header glyphs only when scrolling is actually actionable — not mid-turn.
        bool scrollable = !awaitingResponse;
        scrollHasAbove = scrollable && windowStart > 0;
        scrollHasBelow = scrollable && scrollOffset > 0;

        List<IRenderable> rows = new(contentHeight + inputRows.Count);
        for (int i = windowStart; i < windowEnd; i++)
            rows.Add(contentRows[i]);
        rows.AddRange(inputRows);
        return new Rows(rows);
    }

    /// <summary>
    /// Renders <paramref name="message"/> to single-row markups: <c>"Who: Text"</c> char-wrapped
    /// at <paramref name="termWidth"/>, honouring embedded newlines as hard row breaks (an LLM
    /// reply with <c>...?\n\n#INT#</c> spans three rows even if its char count fits one). The
    /// leading <c>Who:</c> prefix on the very first row is bold; every row is tinted in the speaker
    /// colour (emerald green for Morgana, light green for specialised agents, white for the user).
    /// </summary>
    /// <remarks>
    /// Every emitted <see cref="Markup"/> is a single line no wider than <paramref name="termWidth"/>
    /// visible columns — no <c>\n</c> inside — so Spectre can never inflate it into multiple
    /// terminal rows behind the viewport budget's back. That single-row-per-Markup contract is what
    /// lets <see cref="BuildBody"/> materialise the whole history and slice an exact window of it.
    /// </remarks>
    private static List<IRenderable> RenderMessageRows(DisplayedMessage message, int termWidth)
    {
        List<IRenderable> rows = [];

        // Normalize before counting: trim leading/trailing whitespace and collapse blank-line
        // runs. Morgana authors replies for rich channels (trailing newlines, doubled blank
        // lines); Rune has no markdown renderer to absorb them, and counting one row per '\n'
        // would inflate the content height with invisible phantom rows — lighting the header's
        // ▲ scroll glyph on a conversation that fits the viewport. This is the Grimoire-port
        // adjustment for a channel that carries neither rich cards nor quick replies.
        // Resolve emoji shortcodes (:white_check_mark: → ✅) to real glyphs, mirroring Grimoire's
        // renderers. Rune renders verbatim and has no Markup expansion for shortcodes, so a model
        // that emits GitHub-style codes would otherwise leave them literal on screen. Glyphs are
        // honest plain Unicode (not a "rich" capability), and Rune's rune/cell-based wrapper below
        // measures the resolved glyph correctly. Order: resolve first, then strip variation
        // selectors from the produced glyphs so the cell-width accounting stays exact.
        string text = StripVariationSelectors(BlankRunRegex.Replace(Emoji.Replace(message.Text.Trim()), "\n\n"));
        string fullText = $"{message.Who}: {text}";
        bool first = true;
        foreach (string line in fullText.Split('\n'))
        {
            // Wrap by terminal CELLS (not char count) on whole runes: a wide CJK glyph counts as
            // two columns and a combining mark as zero, so a char-indexed chunk could overflow the
            // row and Spectre would silently wrap it — breaking the one-Markup-per-row contract that
            // BuildBody's scrollback budget depends on.
            foreach (string chunk in ChunkByCells(line, termWidth))
            {
                EmitMessageRow(rows, message, chunk, isFirstRowOfMessage: first);
                first = false;
            }
        }
        return rows;
    }

    /// <summary>Renders a single pre-wrapped row of a message: bold "Who:" prefix on the very first row, same speaker colour without bold on every other row.</summary>
    private static void EmitMessageRow(List<IRenderable> output, DisplayedMessage message, string chunk, bool isFirstRowOfMessage)
    {
        if (isFirstRowOfMessage)
        {
            // First row of the message — bold the "Who:" prefix, then the rest of the
            // row in the same colour but non-bold. If the speaker name itself wraps
            // past the colon, fall back to bolding the whole chunk so the message
            // still visually starts emphasised.
            int colonIdx = chunk.IndexOf(':');
            if (colonIdx > 0 && colonIdx < chunk.Length)
            {
                string who = chunk[..colonIdx];
                string rest = chunk[(colonIdx + 1)..];
                output.Add(new Markup($"[bold {message.Color}]{Markup.Escape(who)}:[/][{message.Color}]{Markup.Escape(rest)}[/]"));
            }
            else
            {
                output.Add(new Markup($"[bold {message.Color}]{Markup.Escape(chunk)}[/]"));
            }
        }
        else
        {
            output.Add(new Markup($"[{message.Color}]{Markup.Escape(chunk)}[/]"));
        }
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
        // the dust ran out; here we give the Rune-honest next step, because Morgana's
        // "start a new one to keep going" is not (yet) actionable inside this CLI —
        // the only way forward is to quit and relaunch the process.
        if (conversationDead)
            return ChunkStyledRows("✦ Conversation spent — press Esc to quit, then relaunch Rune to start fresh", termWidth, $"{ErrorColor} italic");

        if (awaitingResponse)
        {
            string content = $"{currentSpeaker} is thinking…";
            return ChunkStyledRows(content, termWidth, "grey54 italic");
        }

        // Visible layout: chevron, a space, the input runes, then the cursor — packed into rows of
        // at most termWidth CELLS (a wide rune counts as two, so the cursor stays put and rows never
        // wrap silently behind the budget). currentInput is iterated by rune, so a surrogate pair is
        // never split across a wrap boundary. The caret is drawn ON the rune it sits before (inverted
        // block) so it stays visible mid-line, or as a trailing blink '_' when it's at end-of-line —
        // cursorPosition is a char index, mapped to the owning rune by tracking the char offset.
        List<(string Markup, int Cells)> units =
        [
            ($"[{UserColor}]›[/]", 1),
            (" ", 1)
        ];
        int charOffset = 0;
        bool caretPlaced = false;
        foreach (System.Text.Rune rune in currentInput.EnumerateRunes())
        {
            string glyph = Markup.Escape(rune.ToString());
            if (!caretPlaced && cursorPosition >= charOffset && cursorPosition < charOffset + rune.Utf16SequenceLength)
            {
                units.Add(($"[blink {UserColor} invert]{glyph}[/]", RuneCells(rune)));
                caretPlaced = true;
            }
            else
                units.Add((glyph, RuneCells(rune)));
            charOffset += rune.Utf16SequenceLength;
        }
        if (!caretPlaced) // caret at end-of-line
            units.Add(($"[blink {UserColor}]_[/]", 1));

        List<IRenderable> rows = [];
        StringBuilder sb = new(capacity: termWidth + 32 /* slack for style tags */);
        int rowCells = 0;
        foreach ((string markup, int cells) in units)
        {
            // Close the current row before a unit that would overflow it — but never on an empty
            // row, so a unit wider than the whole width still lands somewhere.
            if (rowCells + cells > termWidth && sb.Length > 0)
            {
                rows.Add(new Markup(sb.ToString()));
                sb.Clear();
                rowCells = 0;
            }
            sb.Append(markup);
            rowCells += cells;
        }
        if (sb.Length > 0)
            rows.Add(new Markup(sb.ToString()));
        return rows;
    }

    /// <summary>Splits <paramref name="content"/> into <paramref name="termWidth"/>-cell chunks, each rendered as a single-row <see cref="Markup"/> wrapped in <paramref name="style"/>.</summary>
    private static List<IRenderable> ChunkStyledRows(string content, int termWidth, string style)
    {
        List<IRenderable> rows = [];
        foreach (string chunk in ChunkByCells(content, termWidth))
            rows.Add(new Markup($"[{style}]{Markup.Escape(chunk)}[/]"));
        return rows;
    }

    /// <summary>
    /// Greedy wrap of <paramref name="text"/> at <paramref name="width"/> terminal columns, measured
    /// in cells (Spectre's Wcwidth-backed <c>GetCellWidth</c>: wide CJK count as two, combining
    /// marks/variation selectors as zero) and broken on whole runes so a surrogate pair is never
    /// split. Always returns at least one (possibly empty) slice, so an empty line still emits one row.
    /// </summary>
    private static List<string> ChunkByCells(string text, int width)
    {
        width = Math.Max(1, width);
        if (text.Length == 0)
            return [string.Empty];

        List<string> slices = [];
        StringBuilder current = new();
        int currentCells = 0;
        foreach (System.Text.Rune rune in text.EnumerateRunes())
        {
            int runeCells = RuneCells(rune);
            // Close the current slice before a rune that would overflow the width — but never on an
            // empty slice, otherwise a rune wider than the whole width (pathological) would loop.
            if (currentCells + runeCells > width && current.Length > 0)
            {
                slices.Add(current.ToString());
                current.Clear();
                currentCells = 0;
            }
            current.Append(rune.ToString());
            currentCells += runeCells;
        }
        if (current.Length > 0 || slices.Count == 0)
            slices.Add(current.ToString());
        return slices;
    }

    /// <summary>Terminal cell width of a single rune (0 for combining/zero-width, 2 for wide CJK, 1 otherwise), via Spectre's Wcwidth-backed measurement.</summary>
    private static int RuneCells(System.Text.Rune rune) => rune.ToString().GetCellWidth();

    /// <summary>UTF-16 length (1, or 2 for a surrogate pair) of the rune ending just before <paramref name="pos"/> in <see cref="currentInput"/>. Caller guarantees <paramref name="pos"/> &gt; 0; keeps the caret on a rune boundary going left.</summary>
    private int RuneLengthBefore(int pos) =>
        pos >= 2 && char.IsLowSurrogate(currentInput[pos - 1]) && char.IsHighSurrogate(currentInput[pos - 2]) ? 2 : 1;

    /// <summary>UTF-16 length (1, or 2 for a surrogate pair) of the rune starting at <paramref name="pos"/> in <see cref="currentInput"/>. Caller guarantees <paramref name="pos"/> &lt; length; keeps the caret on a rune boundary going right.</summary>
    private int RuneLengthAt(int pos) =>
        pos + 1 < currentInput.Length && char.IsHighSurrogate(currentInput[pos]) && char.IsLowSurrogate(currentInput[pos + 1]) ? 2 : 1;

    /// <summary>
    /// Removes Unicode variation selectors (U+FE00–U+FE0F): zero-width format codepoints that flip a
    /// base glyph between text and emoji presentation. An emoji-presentation sequence such as <c>⚠️</c>
    /// (<c>⚠</c> + U+FE0F) is measured as two cells by Wcwidth yet rendered as one by most terminals,
    /// which would throw the per-message row budget off by a column. Forcing text presentation keeps
    /// measured and rendered widths in agreement. All selectors are single BMP chars, so a char scan
    /// is surrogate-safe.
    /// </summary>
    private static string StripVariationSelectors(string text)
    {
        bool hasSelector = false;
        foreach (char c in text)
            if (c is >= '\uFE00' and <= '\uFE0F') { hasSelector = true; break; }
        if (!hasSelector)
            return text; // hot path: the overwhelming majority of message text has none

        StringBuilder sb = new(text.Length);
        foreach (char c in text)
            if (c is not (>= '\uFE00' and <= '\uFE0F'))
                sb.Append(c);
        return sb.ToString();
    }

    /// <summary>Maps a speaker name to its palette color: <c>You</c> → white, <c>Morgana</c> → emerald green, everything else (specialised agents) → light green.</summary>
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
    /// speaker's palette color. Rune has no banner widget, so color is the only signal that
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

    /// <summary>A single history row: speaker name, rendered text, and the Spectre color token used for the name.</summary>
    private record DisplayedMessage(string Who, string Text, string Color);
}