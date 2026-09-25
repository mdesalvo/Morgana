using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Morgana.Terminal;
using Morgana.Terminal.Interfaces;
using Morgana.Terminal.Messages;
using Morgana.Terminal.Services;
using Morgana.Contracts;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Rune.Services;

/// <summary>
/// Terminal UI on Spectre.Console LiveDisplay: sticky header (conversation id + speaker),
/// scrolling body (chat history), input buffer at bottom. Webhook messages refresh the UI;
/// keystrokes captured via Console.ReadKey. Input gated during Morgana's turn (Escape exits).
/// </summary>
public sealed class ConsoleUiService : ITerminalUi
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

    /// <summary>
    /// Colour of what a command reports, wherever it ran: neutral grey, so it reads apart from the
    /// speakers (a command is not a turn in the conversation) and apart from a warning, which is
    /// about the conversation rather than about work the user asked for.
    /// </summary>
    private const string CommandReportColor = "grey70";

    /// <summary>
    /// Fallback for <c>Rune:ReplyTimeoutSeconds</c> when absent or non-positive. Generous, because a turn
    /// gives Rune no sign of life at all: with no streaming there is nothing between the message and the
    /// reply, so this measures the whole turn — tool chains and remote colleagues included — where a rich
    /// channel would only be measuring silence between chunks.
    /// </summary>
    private const int DefaultReplyTimeoutSeconds = 180;

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
    /// is always preserved and the sacred user prompt is rendered separately under the
    /// stream and is never sacrificed.
    /// </summary>
    private readonly List<DisplayedMessage> history = [];

    /// <summary>
    /// The rows of the first <see cref="historyRowsMessageCount"/> messages of <see cref="history"/>, wrapped at
    /// <see cref="historyRowsWidth"/>. History only grows, so a frame renders just the messages added since the
    /// last one: a keystroke costs the prompt alone, whatever the length of the conversation.
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

    /// <summary>Thread-safe queue of messages posted by <see cref="WebhookReceiverService"/> awaiting render.</summary>
    private readonly Channel<ChannelMessage> incoming = Channel.CreateUnbounded<ChannelMessage>();

    /// <summary>
    /// How long a turn may stay silent before the prompt is handed back to the user. The message was
    /// accepted by Morgana, so only the delivery is missing: waiting forever would leave Esc as the sole
    /// way out of a conversation that is otherwise healthy. From <c>Rune:ReplyTimeoutSeconds</c>.
    /// </summary>
    private readonly TimeSpan replyTimeout;

    /// <summary>Ends the countdown on the turn in flight the moment Morgana speaks; null when no turn is in flight. Mutated only under <see cref="renderLock"/>.</summary>
    private CancellationTokenSource? replyWatchdog;

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

    /// <summary>Set once the user types <c>/exit</c> or presses <see cref="ConsoleKey.Escape"/>.</summary>
    private volatile bool exitRequested;

    /// <summary>When true, keystrokes (except Esc) are swallowed and the input line shows a thinking hint. Starts true: Rune always waits for Morgana's presentation before the first user turn.</summary>
    private volatile bool awaitingResponse = true;

    /// <summary>
    /// The agent carrying the conversation, null while Morgana holds it herself. Told by the deliveries, kept
    /// apart from the speaker the header shows: one decides what a palette may offer, the other what colour
    /// a row is drawn in. A display choice must never decide which commands exist.
    /// </summary>
    private volatile string? agentCarryingConversation;

    /// <summary>
    /// Latched true when Morgana delivers the terminal dust-exhaustion notice
    /// (<c>ErrorReason == "dust_budget_exhausted"</c>). The conversation is spent: only a command line
    /// allowed on a spent conversation can be typed and the prompt names the two ways out, <c>/new</c>
    /// and Esc. Cleared only when another conversation replaces this one. Mutated only under
    /// <see cref="renderLock"/>; volatile so <see cref="ReadKeysLoop"/> sees it
    /// without taking the lock on every polled keystroke.
    /// </summary>
    private volatile bool conversationDead;

    /// <summary>
    /// Scrollback offset in rows: how far above the live bottom the viewport is anchored.
    /// 0 = pinned to the newest content (the default live view). Only ever non-zero while the
    /// conversation is at rest (<see cref="ReadKeysLoop"/> gates scrolling on
    /// <c>!awaitingResponse</c>, so the window never moves under an in-flight turn); it is
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

    /// <summary>Cell-width measurement this class wraps/truncates against, so a wide glyph never desyncs the row budget BuildBody relies on.</summary>
    private readonly TerminalCellService cells;

    /// <summary>The dropdown of commands shown under the prompt while the line starts with a slash. Called only under <see cref="renderLock"/>.</summary>
    private readonly CommandPaletteService commandPalette;

    /// <summary>The Yes/No question a command that cannot be taken back must have answered first. Called only under <see cref="renderLock"/>.</summary>
    private readonly CommandConfirmationService commandConfirmation;

    /// <summary>The bar a running command reports itself through. Called only under <see cref="renderLock"/>.</summary>
    private readonly CommandProgressService commandProgress;

    /// <summary>The form asking for the values a command declares. Called only under <see cref="renderLock"/>.</summary>
    private readonly CommandOptionPromptService commandOptionPrompt;

    /// <summary>
    /// True while a command is running, whether it works here or on Morgana. A command that opens no turn —
    /// a long export, say — has nothing else telling the screen it is busy, which is what lets its progress
    /// be drawn at all. Volatile: set by the task running the command, read while a frame is drawn.
    /// </summary>
    private volatile bool commandRunning;

    /// <summary>The command running now, named by the hint that holds the prompt until its first frame. Under <see cref="renderLock"/>.</summary>
    private string runningCommandName = string.Empty;

    /// <summary>
    /// What the last command came to, drawn above the prompt and kept out of the transcript: a command is not a
    /// turn of the conversation. Null once the user writes in the prompt, starts a turn or runs another command.
    /// Mutated only under <see cref="renderLock"/>, together with <see cref="commandOutcomeColor"/>.
    /// </summary>
    private string? commandOutcome;

    /// <summary>The colour <see cref="commandOutcome"/> is drawn in: neutral for work done, orange for a refusal, red for a failure.</summary>
    private string commandOutcomeColor = CommandReportColor;

    /// <summary>
    /// The last command started, until its outcome is shown. Morgana's finished frame is accepted only when it
    /// names this command: a late outcome of an earlier one never covers the current one's, while the true
    /// outcome of a command given up on still replaces the deadline's line. Under <see cref="renderLock"/>.
    /// </summary>
    private string? commandOwingOutcome;

    /// <summary>
    /// The name every line written by this side of the conversation is filed under: the channel itself, as
    /// Morgana knows it. What the terminal notices is the channel speaking, never Morgana taking a turn,
    /// so it must be told apart at a glance.
    /// </summary>
    private readonly string channelSpeaker;

    /// <summary>Runs the command the palette resolved.</summary>
    private readonly TerminalCommandRegistryService commandRegistry;

    /// <summary>The conversation on screen: where typed turns go and which deliveries belong on screen. Set when the UI starts.</summary>
    private TerminalSessionService session = null!;

    /// <summary>The live display of the running UI, which the commands' requests repaint. Set once the display starts.</summary>
    private LiveDisplayContext liveContext = null!;

    /// <summary>Fires when the host stops, so a deadline armed by a command dies with the process rather than outliving the live display.</summary>
    private CancellationToken uiStopping;

    /// <summary>Called off when the user leaves, so a command still waiting on Morgana never holds the exit. Set once the display starts.</summary>
    private CancellationTokenSource commandCancellation = new();

    /// <summary>
    /// Completes when deliveries may be drawn. Pending while another conversation is being opened, so its
    /// presentation cannot land on the transcript about to be cleared; <see cref="DrainIncomingLoop"/> waits on it.
    /// </summary>
    private Task deliveriesReleased = Task.CompletedTask;

    /// <summary>Serializes mutations of <see cref="history"/>, <see cref="currentInput"/>, <see cref="currentSpeaker"/> and the paired <see cref="LiveDisplayContext.UpdateTarget"/>/<see cref="LiveDisplayContext.Refresh"/> calls. The resize callback runs on its own thread (SIGWINCH handler / polling task) and would otherwise race with <see cref="ReadKeysLoop"/> and <see cref="DrainIncomingLoop"/> over the shared list. Uses <see cref="System.Threading.Lock"/> (.NET 9+) instead of <c>object</c> so the compiler emits the optimised primitive and rejects misuse (e.g. passing the lock as an <c>object</c>).</summary>
    private readonly Lock renderLock = new();

    /// <summary>Reads the reply deadline and the input cap. Captures the resize watcher, the cell measurement, the command palette and the command registry.</summary>
    public ConsoleUiService(IConfiguration configuration, IViewportResizeWatcher viewportResizeWatcher, TerminalCellService cells, CommandPaletteService commandPalette, CommandConfirmationService commandConfirmation, CommandProgressService commandProgress, CommandOptionPromptService commandOptionPrompt, TerminalCommandRegistryService commandRegistry, ChannelProfile profile)
    {
        // A non-positive wait would declare every turn lost the instant it is sent, so it falls back too
        int replyTimeoutSeconds = configuration.GetValue<int?>("Rune:ReplyTimeoutSeconds") ?? DefaultReplyTimeoutSeconds;
        replyTimeout = TimeSpan.FromSeconds(replyTimeoutSeconds > 0 ? replyTimeoutSeconds : DefaultReplyTimeoutSeconds);
        this.viewportResizeWatcher = viewportResizeWatcher;
        this.cells = cells;
        this.commandPalette = commandPalette;
        this.commandConfirmation = commandConfirmation;
        this.commandProgress = commandProgress;
        this.commandOptionPrompt = commandOptionPrompt;
        channelSpeaker = profile.ChannelName;
        this.commandRegistry = commandRegistry;

        // Non-positive (or absent) falls back to 500 so a misconfiguration can't lock the prompt shut.
        maxInputLength = configuration.GetValue<int?>("Rune:MaxInputLength") ?? 500;
        if (maxInputLength <= 0)
            maxInputLength = 500;
    }

    /// <summary>Called by <see cref="WebhookReceiverService"/> when Morgana delivers a message.</summary>
    public void EnqueueIncoming(ChannelMessage message) => incoming.Writer.TryWrite(message);

    /// <summary>
    /// Starts the live terminal UI. Returns when the user quits or the cancellation token fires.
    /// Committed input lines go to the conversation <paramref name="terminalSession"/> names.
    /// </summary>
    public async Task RunAsync(
        TerminalSessionService terminalSession,
        CancellationToken cancellationToken = default)
    {
        // Lock even here (pre-threading, zero contention) so all fields read by
        // BuildLayout/BuildHeader/AppendHistoryTail are always accessed under renderLock —
        // keeps Rider's inconsistent-sync analysis clean.
        Layout layout;
        lock (renderLock)
        {
            // The session outlives any one conversation; the header shows the one open as the UI starts
            session = terminalSession;
            conversationId = terminalSession.ConversationId;
            layout = BuildLayout();
        }

        // Spectre's Live rendering only ever writes/diffs content — it never touches the terminal's
        // own native cursor. Left visible, that cursor sits wherever the last partial repaint's
        // cursor-position escape happened to leave it (often the start of whatever row Spectre wrote
        // last), showing up as a stray blinking block in the middle of the scrollback that has
        // nothing to do with the blinking "_"/inverted-block caret Rune draws itself at the prompt.
        // Hidden for the whole Live session, restored in the finally below so the user's shell
        // prompt gets its cursor back on exit.
        AnsiConsole.Cursor.Hide();
        try
        {
            await RunLiveDisplayAsync(layout, cancellationToken);
        }
        finally
        {
            AnsiConsole.Cursor.Show();
        }
    }

    /// <summary>The actual Live(Layout) session — split out of <see cref="RunAsync"/> so the cursor hide/show wrap above reads as a single, obvious guard rather than being buried inside the lambda.</summary>
    private async Task RunLiveDisplayAsync(Layout layout, CancellationToken cancellationToken)
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
                // unsubscribes when the UI loop exits (normal quit, Esc, cancel
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
                // conversation id and no sign that Rune is waiting on anything.
                lock (renderLock)
                    ctx.Refresh();

                uiStopping = cancellationToken;
                liveContext = ctx;

                // Commands die with the process as well as with the user leaving
                commandCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

                Task readLoop = Task.Run(() => ReadKeysLoop(ctx, cancellationToken), cancellationToken);
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
                // A conversation being swapped in holds its deliveries until the screen has been cleared for it,
                // then whatever the conversation left behind still queued is dropped
                await Volatile.Read(ref deliveriesReleased);
                if (!session.IsConversationOnScreen(message.ConversationId))
                    continue;

                // A frame of a command run on Morgana is chrome: the bar holds the prompt while the command
                // works, then the finished frame's text becomes its outcome. Nothing of it reaches the transcript
                if (message.Progress is { } progressFrame)
                {
                    HandleInboundProgress(ctx, message, progressFrame);
                    continue;
                }

                // A command's outcome always travels on its finished frame: a command line naming no command
                // belongs to nothing on screen and has no place in the transcript either
                if (message.MessageType == "system")
                    continue;

                // Attribute the row to whoever authored it: a specialised agent keeps its
                // own colour even on the farewell line that carries AgentCompleted=true.
                // Sanitized once here — message.AgentName and message.Text both arrive over
                // the webhook from Morgana, so neither is trusted terminal input; every
                // downstream use (history, header, courtesy line) reuses this cleaned value
                // instead of re-reading the raw field.
                string messageSpeaker = string.IsNullOrWhiteSpace(message.AgentName)
                    ? "Morgana"
                    : TerminalCellService.StripControlCharacters(message.AgentName);

                lock (renderLock)
                {
                    history.Add(new DisplayedMessage(messageSpeaker, SanitizeMessageText(message.Text), RowColor(message, messageSpeaker)));

                    // Revert the sticky header to Morgana on completion so the next user
                    // turn doesn't render under the outgoing agent's colour.
                    currentSpeaker = message.AgentCompleted || string.IsNullOrWhiteSpace(message.AgentName)
                        ? "Morgana"
                        : messageSpeaker;

                    // An agent that signalled completion has handed the conversation back, so nothing
                    // addressed at the agent carrying it applies any more; a reply from Morgana says the same
                    agentCarryingConversation = message.AgentCompleted || !IsSpecializedAgent(message.AgentName)
                        ? null
                        : messageSpeaker;

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
                    // it BEFORE wasting a keystroke. The red banner line above stays as
                    // Morgana's canonical word and BuildInputRows replaces the prompt with
                    // the way to act on it here: /new for a fresh conversation or Esc to leave.
                    if (string.Equals(message.ErrorReason, "dust_budget_exhausted", StringComparison.Ordinal))
                        MarkConversationSpent();

                    // Release the input gate: ReadKeysLoop was swallowing keystrokes until
                    // this first webhook delivery landed. (No-op once conversationDead:
                    // ReadKeysLoop keeps swallowing on the dead latch regardless.)
                    // Morgana has spoken, so the turn is no longer at risk of being declared lost.
                    CancelReplyWatchdog();
                    ReleaseTurn();
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

    /// <summary>Polls <see cref="Console.KeyAvailable"/> every 25 ms and dispatches keys: on a command line the palette takes the arrows, Tab and Esc and Enter runs the command; otherwise Enter commits, Backspace edits, Esc exits, printable chars append to the buffer.</summary>
    private async Task ReadKeysLoop(LiveDisplayContext ctx, CancellationToken cancellationToken)
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
                // conversation loop returns as well. The exit flag stays untouched — nobody typed /exit.
                incoming.Writer.TryComplete();
                return;
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                exitRequested = true;
                incoming.Writer.TryComplete();
                return;
            }

            // A command waiting on a Yes or a No owns the keyboard: until it is answered nothing else may be
            // typed, run or exited, so the question cannot be walked away from by accident
            if (TryHandleConfirmationKey(ctx, key))
                continue;

            // A form being filled in gives Esc back to the user as "drop this command", which is what the key
            // means everywhere else a command surface is open; the rest of the line editing stays the prompt's
            if (key.Key == ConsoleKey.Escape && TryCancelOptionPrompt(ctx))
                continue;

            // The palette owns the arrows, Tab and Esc while a command line is being typed, so it comes before
            // the scrollback that would otherwise take the arrows and the exit Esc otherwise means
            if (TryHandleCommandPaletteKey(ctx, key.Key))
                continue;

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
            // Morgana to speak; forever once the conversation is dust-dead (a
            // one-way latch: no point typing into a budget the backend will reject).
            // Esc is always honoured so the user can bail out even mid-turn or quit a
            // spent conversation; everything else (printable chars, Enter, Backspace)
            // is a no-op.
            // Lock-free read is intentional: awaitingResponse and conversationDead are both
            // volatile, guaranteeing visibility. Taking renderLock here on every polled
            // keystroke would cause unnecessary contention with DrainIncomingLoop.
            // A spent conversation still takes a command line, the only way out of it short of Esc.
            // A running command holds the prompt as a turn does: a second one started meanwhile would race it
            // for the widget and the outcome line
            // ReSharper disable InconsistentlySynchronizedField
            if ((awaitingResponse || commandRunning || (conversationDead && !AcceptsKeyOnSpentConversation(key))) && key.Key != ConsoleKey.Escape)
            // ReSharper restore InconsistentlySynchronizedField
                continue;

            switch (key.Key)
            {
                case ConsoleKey.Enter:
                {
                    // Snapshot-and-clear before awaiting: any late keystroke during the send
                    // must land on a fresh buffer, not reappend to the line we just sent.
                    // Length check is inside the lock — DrainIncomingLoop also writes
                    // currentInput (under lock), so the when-guard read would be a cross-thread
                    // race if left outside. The command is resolved against the line before it
                    // is cleared, since the palette's highlight belongs to that line.
                    string toSend;
                    CommandDescriptor? command = null;
                    CommandInvocation? filledInvocation = null;
                    bool wasFillingIn;
                    lock (renderLock)
                    {
                        toSend = currentInput;

                        // A command being filled in owns the line: what was typed answers the option on screen
                        // rather than opening a turn or a new command
                        wasFillingIn = commandOptionPrompt.IsActive;
                        if (wasFillingIn)
                            filledInvocation = commandOptionPrompt.Accept(toSend);
                        else if (CommandPaletteService.IsCommandLine(toSend))
                            command = commandPalette.ResolveCommandToRun(toSend, ConversationState());
                        currentInput = string.Empty;
                        cursorPosition = 0;

                        // The next question opens on that option's default, ready to be taken as it stands
                        if (wasFillingIn && filledInvocation is null)
                        {
                            currentInput = commandOptionPrompt.CurrentOptionDefault;
                            cursorPosition = currentInput.Length;
                        }
                    }

                    if (wasFillingIn)
                    {
                        // Either the form has everything it asked for, or it is still asking: both are drawn by it
                        if (filledInvocation is { } readyInvocation)
                            _ = RunFilledCommandAsync(ctx, readyInvocation);
                        else
                            RefreshLayout(ctx);
                        break;
                    }

                    if (toSend.Length == 0) break;

                    // A command line never reaches Morgana as prose: it runs a command or is refused here
                    // The command runs beside this loop, so Esc is still read while it waits on Morgana
                    if (CommandPaletteService.IsCommandLine(toSend))
                    {
                        _ = RunCommandLineAsync(ctx, toSend, command);
                        break;
                    }

                    // Prose on a spent conversation would only be refused by Morgana: a line edited out of
                    // command mode is dropped with a notice saying why
                    if (conversationDead)
                    {
                        ShowSystemNotice(ctx, "The conversation is spent: type /new to start a fresh one", WarningColor);
                        break;
                    }

                    string line = toSend;
                    await SubmitTurnAsync(ctx, line, () => session.SendUserTurnAsync(line));
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
                            commandOutcome = null;
                            RefreshWhenInputSettles(ctx);
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
                            commandOutcome = null;
                            RefreshWhenInputSettles(ctx);
                        }
                    }
                    break;
                case ConsoleKey.LeftArrow:
                    lock (renderLock)
                    {
                        if (cursorPosition > 0)
                        {
                            cursorPosition -= RuneLengthBefore(cursorPosition);
                            RefreshWhenInputSettles(ctx);
                        }
                    }
                    break;
                case ConsoleKey.RightArrow:
                    lock (renderLock)
                    {
                        if (cursorPosition < currentInput.Length)
                        {
                            cursorPosition += RuneLengthAt(cursorPosition);
                            RefreshWhenInputSettles(ctx);
                        }
                    }
                    break;
                case ConsoleKey.Escape:
                    // Exit outside the palette — completes the channel so DrainIncomingLoop
                    // unblocks out of its await foreach and RunAsync can return.
                    RequestExit();
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

                                // The user has moved on from the last command: its outcome makes room for what is typed
                                commandOutcome = null;
                                RefreshWhenInputSettles(ctx);
                            }
                        }
                    }
                    break;
            }
        }
    }

    /// <summary>Gives <paramref name="key"/> to the question a command is waiting on; false when no command is.</summary>
    private bool TryHandleConfirmationKey(LiveDisplayContext ctx, ConsoleKeyInfo key)
    {
        CommandInvocation? questionedInvocation;
        ConfirmationOutcome outcome;
        lock (renderLock)
        {
            if (!commandConfirmation.IsPending)
                return false;

            // Which command the answer belongs to is only readable while the question is still open
            questionedInvocation = commandConfirmation.PendingInvocation;
            outcome = commandConfirmation.HandleKey(key);
            ctx.UpdateTarget(BuildLayout());
            ctx.Refresh();
        }

        if (questionedInvocation is null)
            return true;

        switch (outcome)
        {
            case ConfirmationOutcome.Confirmed:
                // The command runs beside this loop, so Esc is still read while it waits on Morgana
                _ = RunCommandAsync(ctx, questionedInvocation, confirmed: true);
                break;
            case ConfirmationOutcome.Declined:
                // A command that leaves no trace of having been asked for would read as a swallowed keystroke
                ShowCommandOutcome(ctx, $"/{questionedInvocation.Command.Name} was not run", WarningColor);
                break;
        }
        return true;
    }

    /// <summary>Drops the command being filled in, with whatever it had gathered; false when no form is open.</summary>
    private bool TryCancelOptionPrompt(LiveDisplayContext ctx)
    {
        CommandDescriptor? abandoned;
        lock (renderLock)
        {
            if (!commandOptionPrompt.IsActive)
                return false;

            // Named in the notice below, since the line that opened the form is long gone from the prompt
            abandoned = commandOptionPrompt.PendingCommand;
            commandOptionPrompt.Cancel();
            currentInput = string.Empty;
            cursorPosition = 0;
            ctx.UpdateTarget(BuildLayout());
            ctx.Refresh();
        }

        ShowCommandOutcome(ctx, abandoned is null ? "command cancelled" : $"/{abandoned.Name} was not run", WarningColor);
        return true;
    }

    /// <summary>Gives the arrows, Tab and Esc to the palette while a command line is typed at rest; false leaves the key to the loop.</summary>
    private bool TryHandleCommandPaletteKey(LiveDisplayContext ctx, ConsoleKey key)
    {
        if (key is not (ConsoleKey.UpArrow or ConsoleKey.DownArrow or ConsoleKey.Tab or ConsoleKey.Escape))
            return false;

        lock (renderLock)
        {
            // No palette while Morgana is answering or outside a command line: the arrows scroll and Esc leaves.
            // A form asking for a value is not a command line either, whatever the value happens to start with:
            // a path opens with a slash and must not turn the prompt into a command picker
            if (awaitingResponse || commandRunning || commandOptionPrompt.IsActive || !CommandPaletteService.IsCommandLine(currentInput))
                return false;

            switch (key)
            {
                // Up walks towards the best match, down away from it, wrapping at both ends
                case ConsoleKey.UpArrow:
                    commandPalette.MoveHighlight(currentInput, ConversationState(), -1);
                    break;
                case ConsoleKey.DownArrow:
                    commandPalette.MoveHighlight(currentInput, ConversationState(), 1);
                    break;
                case ConsoleKey.Tab:
                    // The caret follows to the end of the completed name, ready for Enter
                    if (commandPalette.CompleteHighlightedCommand(currentInput, ConversationState()) is { } completed)
                    {
                        currentInput = completed;
                        cursorPosition = completed.Length;
                    }
                    break;
                case ConsoleKey.Escape:
                    // Dismissing the palette drops the line with it; a second Esc, now outside it, leaves Rune
                    currentInput = string.Empty;
                    cursorPosition = 0;
                    break;
            }

            ctx.UpdateTarget(BuildLayout());
            ctx.Refresh();
            return true;
        }
    }

    /// <summary>Tells whether a spent conversation lets <paramref name="key"/> through: a slash on an empty prompt, anything once a line is started.</summary>
    private bool AcceptsKeyOnSpentConversation(ConsoleKeyInfo key)
    {
        // A line already started must stay editable and runnable, even if the caret wandered before its slash
        lock (renderLock)
            return currentInput.Length > 0 || key.KeyChar == '/';
    }

    /// <summary>Runs the command resolved from <paramref name="line"/>; a missing or failing one is explained above the prompt.</summary>
    private async Task RunCommandLineAsync(LiveDisplayContext ctx, string line, CommandDescriptor? command)
    {
        if (command is null)
        {
            // On a spent conversation the likely mistake is reaching for a command that needs Morgana
            ShowCommandOutcome(ctx, conversationDead
                ? $"{line.Trim()} is not available on a spent conversation: type /new to start a fresh one"
                : $"{line.Trim()} is not a command: pick one with ↑↓ or type the start of its name", WarningColor);
            return;
        }

        // The values are read off the line the user typed, then judged by what the command says it takes:
        // a line that cannot be read runs nothing, since the missing half would be guessed at
        IReadOnlyDictionary<string, string> options = CommandLineParser.ParseOptions(line, out string? lineProblem);
        if (lineProblem is { } malformedLine)
        {
            ShowCommandOutcome(ctx, malformedLine, WarningColor);
            return;
        }

        // A command run without what it declares asks for it: the description it publishes is exactly the
        // question to put on screen, so the user is walked through instead of being sent back to the line
        if (CommandOptionPromptService.NeedsFillingIn(command, options))
        {
            lock (renderLock)
            {
                commandOptionPrompt.Begin(command, options);

                // The first question opens on that option's default, which is what running it blind would use
                currentInput = commandOptionPrompt.CurrentOptionDefault;
                cursorPosition = currentInput.Length;
                ctx.UpdateTarget(BuildLayout());
                ctx.Refresh();
            }
            return;
        }

        if (command.DescribeOptionProblem(options) is { } problem)
        {
            ShowCommandOutcome(ctx, problem, WarningColor);
            return;
        }

        CommandInvocation invocation = new(command, options);

        // A command that cannot be taken back puts its question on the prompt; whether it ever runs is the answer's business
        if (command.RequiresConfirmation)
        {
            AskForConfirmation(ctx, invocation);
            return;
        }

        await RunCommandAsync(ctx, invocation, confirmed: false);
    }

    /// <summary>Runs what the form gathered, asking for confirmation first when the command wants one.</summary>
    private async Task RunFilledCommandAsync(LiveDisplayContext ctx, CommandInvocation invocation)
    {
        if (invocation.Command.RequiresConfirmation)
        {
            AskForConfirmation(ctx, invocation);
            return;
        }

        await RunCommandAsync(ctx, invocation, confirmed: false);
    }

    /// <summary>Repaints the screen for a change the caller has already made under the render lock.</summary>
    private void RefreshLayout(LiveDisplayContext ctx)
    {
        lock (renderLock)
        {
            ctx.UpdateTarget(BuildLayout());
            ctx.Refresh();
        }
    }

    /// <summary>Puts <paramref name="invocation"/>'s Yes/No question on the prompt, where it stays until it is answered.</summary>
    private void AskForConfirmation(LiveDisplayContext ctx, CommandInvocation invocation)
    {
        lock (renderLock)
        {
            commandConfirmation.Ask(invocation);
            ctx.UpdateTarget(BuildLayout());
            ctx.Refresh();
        }
    }

    /// <summary>Runs <paramref name="invocation"/>, carrying the <paramref name="confirmed"/> answer it asked for, while the prompt waits on it; a failure is explained above the prompt.</summary>
    private async Task RunCommandAsync(LiveDisplayContext ctx, CommandInvocation invocation, bool confirmed)
    {
        lock (renderLock)
        {
            // One command at a time: a second one would race the first for the widget and the outcome line
            if (commandRunning)
                return;
            commandRunning = true;
            runningCommandName = invocation.Command.Name;
            commandOwingOutcome = invocation.Command.Name;

            // The previous command's outcome gives way to the hint naming this one, as does a bar an earlier
            // command left behind when its call was given up on
            commandOutcome = null;
            commandProgress.Clear();
            ctx.UpdateTarget(BuildLayout());
            ctx.Refresh();
        }

        try
        {
            // The command decides what happens next through this UI: leave, report or swap the conversation
            await commandRegistry.ExecuteCommandAsync(invocation, this, confirmed, commandCancellation.Token);
        }
        catch (OperationCanceledException) when (commandCancellation.IsCancellationRequested)
        {
            // Rune is shutting down: the command dies with it
        }
        catch (TimeoutException ex)
        {
            // The command's deadline already names the command and says it was called off. An outcome that
            // landed just before it is the truth and stays: Morgana had finished the work
            lock (renderLock)
            {
                if (string.Equals(commandOwingOutcome, invocation.Command.Name, StringComparison.OrdinalIgnoreCase))
                    ShowCommandOutcome(ctx, ex.Message, ErrorColor);
            }
        }
        catch (Exception ex)
        {
            // Logging is silenced under the live UI, so the outcome line is the only place the failure can surface
            ShowCommandOutcome(ctx, $"/{invocation.Command.Name} failed: {ex.Message}", ErrorColor);
        }
        finally
        {
            // The command is over: whatever it was drawing goes with it, one that died halfway through included.
            // Only /new leaves a turn open behind it, waiting on the fresh conversation's presentation
            commandRunning = false;
            lock (renderLock)
            {
                if (!awaitingResponse)
                    commandProgress.Clear();

                // The prompt comes back whatever the command left: the hint holding it goes. An outcome that
                // landed while the command still ran is drawn only now. After the user left there is no screen
                if (!exitRequested)
                {
                    ctx.UpdateTarget(BuildLayout());
                    ctx.Refresh();
                }
            }
        }
    }

    /// <summary>
    /// The conversation as the palette must judge it: whether the budget is spent and whether an agent is
    /// carrying it. The agent is read off the speaker the header names, which goes back to Morgana the moment
    /// one hands the conversation over. Read under <see cref="renderLock"/> by every caller.
    /// </summary>
    private TerminalConversationState ConversationState() =>
        new(conversationDead, agentCarryingConversation is not null);

    /// <summary>Opens a turn on the wire, from which the silence deadline is measured. Must be called under <see cref="renderLock"/>.</summary>
    private void BeginTurn()
    {
        awaitingResponse = true;

        // The conversation has moved on, so what the last command came to is no longer news
        commandOutcome = null;
    }

    /// <summary>
    /// Gives the prompt back at the end of a turn, however it ended: answered, failed or given up on. A bar
    /// a command left standing belongs to work that is over, so it goes with the turn rather than sitting
    /// where the caret is drawn — a command that dies without its closing frame cannot hold the screen.
    /// Must be called under <see cref="renderLock"/>.
    /// </summary>
    private void ReleaseTurn()
    {
        awaitingResponse = false;
        commandProgress.Clear();
    }

    /// <summary>Writes a system line into the transcript in <paramref name="color"/> and brings the view back to it.</summary>
    private void ShowSystemNotice(LiveDisplayContext ctx, string text, string color)
    {
        lock (renderLock)
        {
            // A command still finishing after the user left must not draw over the shell the display gave back
            if (exitRequested)
                return;

            history.Add(new DisplayedMessage(channelSpeaker, text, color));

            // A notice scrolled out of sight would leave the user wondering why nothing happened
            scrollOffset = 0;
            ctx.UpdateTarget(BuildLayout());
            ctx.Refresh();
        }
    }

    /// <inheritdoc />
    public void RequestExit()
    {
        // The key loop stops on the flag; completing the queue releases the drain loop, so the live display returns.
        // A command still waiting on Morgana is called off, so leaving never waits for it.
        exitRequested = true;
        commandCancellation.Cancel();
        incoming.Writer.TryComplete();
    }

    /// <inheritdoc />
    public void ShowProgress(CommandProgress frame)
    {
        lock (renderLock)
        {
            commandProgress.Show(frame);
            liveContext.UpdateTarget(BuildLayout());
            liveContext.Refresh();
        }
    }

    /// <inheritdoc />
    public void ShowCommandOutcome(string text, bool isFailure = false)
    {
        lock (renderLock)
        {
            // A command run here reports its own outcome: nothing is left owed to it from Morgana
            commandOwingOutcome = null;
            ShowCommandOutcome(liveContext, text, isFailure ? ErrorColor : CommandReportColor);
        }
    }

    /// <summary>
    /// Takes a frame of a command run on Morgana: its bar while the command runs, then the finished frame's
    /// text as the command's outcome.
    /// </summary>
    private void HandleInboundProgress(LiveDisplayContext ctx, ChannelMessage message, CommandProgress frame)
    {
        lock (renderLock)
        {
            // Only the command still owed an outcome is heard. Its bar only while it runs: a frame landing after
            // the command returned would stand over the prompt with nothing left to take it down. Its outcome
            // whenever it lands, since the drain may deliver it after the call returned or after its deadline
            if (!string.Equals(frame.Command, commandOwingOutcome, StringComparison.OrdinalIgnoreCase) || (!frame.Finished && !commandRunning))
                return;

            commandProgress.Show(frame);
            if (frame.Finished)
            {
                SetCommandOutcome(message.Text, OutcomeColor(message));
                commandOwingOutcome = null;

                // A budget found spent ends the conversation whatever asked for more of it, a command included
                if (string.Equals(message.ErrorReason, "dust_budget_exhausted", StringComparison.Ordinal))
                    MarkConversationSpent();
            }

            ctx.UpdateTarget(BuildLayout());
            ctx.Refresh();
        }
    }

    /// <summary>Latches the conversation as spent: the prompt takes only the ways out of it. Must be called under <see cref="renderLock"/>.</summary>
    private void MarkConversationSpent()
    {
        conversationDead = true;
        currentInput = string.Empty; // discard any half-typed doomed line
        cursorPosition = 0;
    }

    /// <summary>
    /// The colour of an outcome Morgana delivered: a refusal by the rate limit reads as a warning, a spent budget
    /// as an error and anything else as the command's own report.
    /// </summary>
    private static string OutcomeColor(ChannelMessage message) => message.ErrorReason switch
    {
        "rate_limit_exceeded" => WarningColor,
        "dust_budget_exhausted" => ErrorColor,
        _ => CommandReportColor
    };

    /// <summary>Draws <paramref name="text"/> above the prompt in <paramref name="color"/> as the outcome of the command just run.</summary>
    private void ShowCommandOutcome(LiveDisplayContext ctx, string text, string color)
    {
        lock (renderLock)
        {
            // A command still finishing after the user left must not draw over the shell the display gave back
            if (exitRequested)
                return;

            SetCommandOutcome(text, color);
            ctx.UpdateTarget(BuildLayout());
            ctx.Refresh();
        }
    }

    /// <summary>Records what a command came to, cleaned of anything the terminal would obey. Must be called under <see cref="renderLock"/>.</summary>
    private void SetCommandOutcome(string text, string color)
    {
        // Morgana's outcome arrives over the unauthenticated callback, so it is drawn only once it is plain text
        commandOutcome = TerminalCellService.StripControlCharacters(text);
        commandOutcomeColor = color;
    }

    /// <summary>Echoes <paramref name="echo"/> as the user's line, then sends the turn through <paramref name="dispatch"/> under the reply deadline.</summary>
    private async Task SubmitTurnAsync(LiveDisplayContext ctx, string echo, Func<Task> dispatch)
    {
        lock (renderLock)
        {
            // Echo and thinking hint go up before the send, so a slow backend still feels answered
            history.Add(new DisplayedMessage("You", echo, UserColor));
            BeginTurn();
            scrollOffset = 0; // jump back to the live bottom for the new turn
            ctx.UpdateTarget(BuildLayout());
            ctx.Refresh();
        }

        try
        {
            await dispatch();

            // Morgana took the turn: from here only its delivery can reopen the prompt,
            // so give that delivery a deadline. A reply already landed means no turn to watch.
            lock (renderLock)
            {
                if (awaitingResponse)
                    StartReplyWatchdog(ctx, uiStopping);
            }
        }
        catch (Exception ex)
        {
            // Surface the failure in-UI (logging is silenced) and release the
            // gate so the user can retry without waiting for a webhook that
            // will never arrive. A send called off because the user left draws nothing.
            lock (renderLock)
            {
                if (exitRequested)
                    return;
                history.Add(new DisplayedMessage(channelSpeaker, $"send failed: {ex.Message}", ErrorColor));
                ReleaseTurn();
                ctx.UpdateTarget(BuildLayout());
                ctx.Refresh();
            }
        }
    }

    /// <inheritdoc />
    public async Task ReplaceConversationAsync(Func<CancellationToken, Task<string>> openConversation, CancellationToken cancellationToken)
    {
        LiveDisplayContext ctx = liveContext;

        // The new conversation's presentation may land before its id is known here: it waits in the queue
        // until the screen has been cleared for it, instead of joining the transcript about to be wiped
        TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref deliveriesReleased, released.Task);
        try
        {
            // The prompt waits while the conversation is opened, as it waits for a reply
            lock (renderLock)
            {
                BeginTurn();
                scrollOffset = 0;
                ctx.UpdateTarget(BuildLayout());
                ctx.Refresh();
            }

            string openedConversationId;
            try
            {
                openedConversationId = await openConversation(cancellationToken);
            }
            catch
            {
                // The conversation on screen is still the one being talked to: the prompt comes back to it,
                // unless the user left meanwhile and there is no screen any more
                lock (renderLock)
                {
                    if (exitRequested)
                        throw;
                    ReleaseTurn();
                    ctx.UpdateTarget(BuildLayout());
                    ctx.Refresh();
                }

                // The command reports why, through the failure notice of RunCommandLineAsync
                throw;
            }

            lock (renderLock)
            {
                // Opened just as the user left: the lifecycle ends whichever conversation the session names by then
                if (exitRequested)
                    return;
                ResetScreenForConversation(openedConversationId);

                // The fresh conversation's presentation is a reply like any other and gets the same deadline
                BeginTurn();
                StartReplyWatchdog(ctx, uiStopping);
                ctx.UpdateTarget(BuildLayout());
                ctx.Refresh();
            }
        }
        finally
        {
            // Deliveries resume however the swap ended: the drain loop drops those of a conversation not on screen
            released.TrySetResult();
        }
    }

    /// <summary>Clears everything the previous conversation left on screen and names <paramref name="openedConversationId"/> in the header. Under <see cref="renderLock"/>.</summary>
    private void ResetScreenForConversation(string openedConversationId)
    {
        // A deadline armed for the old conversation must not fire its notice on the new one
        CancelReplyWatchdog();

        // The transcript and its wrapped rows go together: the next frame must not draw rows of a gone history
        history.Clear();
        historyRows.Clear();
        historyRowsMessageCount = 0;

        // Header back to base Morgana; the gauge stays hidden until the new conversation reports its dust
        currentSpeaker = "Morgana";
        agentCarryingConversation = null;
        conversationId = openedConversationId;
        _dustSegment = string.Empty;

        // A fresh budget: the spent latch of the old conversation is gone
        conversationDead = false;

        // The view and the prompt start clean, the /new line and any command outcome included
        commandOutcome = null;
        scrollOffset = 0;
        currentInput = string.Empty;
        cursorPosition = 0;
    }

    /// <summary>
    /// Gives the turn just sent <see cref="replyTimeout"/> to be answered. Past it the user gets the prompt
    /// back with an honest notice: a delivery Morgana never makes, because it failed on its side or cannot
    /// reach the callback, would otherwise lock the conversation for the rest of the process's life.
    /// Must be called under <see cref="renderLock"/>.
    /// </summary>
    private void StartReplyWatchdog(LiveDisplayContext ctx, CancellationToken cancellationToken)
    {
        CancelReplyWatchdog();
        CancellationTokenSource watchdog = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        replyWatchdog = watchdog;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(replyTimeout, watchdog.Token);
            }
            catch (OperationCanceledException)
            {
                // Morgana answered in time, or the process is going down: whoever called the deadline off owns it
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

                history.Add(new DisplayedMessage(
                    channelSpeaker,
                    $"no answer from Morgana after {replyTimeout.TotalSeconds:0}s — the turn may be lost, ask again or press Esc to quit",
                    ErrorColor));
                ReleaseTurn();
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
    /// Repaints the screen unless further keystrokes are already queued. A pasted line reaches Rune one
    /// glyph at a time and only its settled state is worth showing, so a long paste costs one frame
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
        // while awaiting a reply and NOT on the dead latch and only once at least one char is in
        // the buffer (Rune has no quick-reply mode, so there's no QR case to exclude). It's the
        // prompt's own "dust gauge": white → orange at ≥70% → red at ≥100% (reusing the dust
        // palette), so a line creeping toward the cap telegraphs the same unease as a depleting
        // budget and the swallowed keystrokes at the top read as "you hit the cap", not a glitch.
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

        // The conversation as single rows, then a window of it. The input row(s) are NOT part of the
        // stream: they stay pinned at the bottom (the sacred prompt), so the history gets whatever
        // height the input leaves free.
        RenderNewHistoryRows(termWidth);
        List<IRenderable> contentRows = historyRows;

        // The command palette hangs under the prompt and takes only the rows the prompt leaves, so a short
        // terminal loses palette rows before it loses the caret
        // A form asking for a value keeps the rows under the prompt: a path starting with a slash would
        // otherwise list every command underneath the question it is answering
        List<Markup> paletteRows = awaitingResponse || commandOptionPrompt.IsActive
            ? []
            : commandPalette.RenderPalette(currentInput, ConversationState(), termWidth);
        int paletteBudget = Math.Max(0, bodyHeight - inputRows.Count);
        if (paletteRows.Count > paletteBudget)
            paletteRows = paletteRows.GetRange(0, paletteBudget);

        int contentHeight = Math.Max(0, bodyHeight - inputRows.Count - paletteRows.Count);

        // Anchor the window. scrollOffset counts rows up from the bottom; clamp it to the live
        // content so a resize or a shorter conversation can't strand the viewport off the end.
        // Scrolling is only enabled at rest (see ReadKeysLoop), so during a turn the offset is 0
        // and this pins to the bottom.
        int maxOffset = Math.Max(0, contentRows.Count - contentHeight);
        scrollOffset = Math.Clamp(scrollOffset, 0, maxOffset);
        int windowEnd = contentRows.Count - scrollOffset;
        int windowStart = Math.Max(0, windowEnd - contentHeight);

        // Light the header glyphs only when scrolling is actually actionable — not mid-turn.
        bool scrollable = !awaitingResponse;
        scrollHasAbove = scrollable && windowStart > 0;
        scrollHasBelow = scrollable && scrollOffset > 0;

        List<IRenderable> rows = new(contentHeight + inputRows.Count + paletteRows.Count);
        for (int i = windowStart; i < windowEnd; i++)
            rows.Add(contentRows[i]);
        rows.AddRange(inputRows);
        rows.AddRange(paletteRows);
        return new Rows(rows);
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
    /// Renders <paramref name="message"/> to single-row markups: <c>"Who: Text"</c> char-wrapped
    /// at <paramref name="termWidth"/>, honouring embedded newlines as hard row breaks (an LLM
    /// reply with <c>...?\n\nAnything else?</c> spans three rows even if its char count fits one). The
    /// leading <c>Who:</c> prefix on the very first row is bold; every row is tinted in the speaker
    /// colour (emerald green for Morgana, light green for specialised agents, white for the user).
    /// </summary>
    /// <remarks>
    /// Every emitted <see cref="Markup"/> is a single line no wider than <paramref name="termWidth"/>
    /// visible columns — no <c>\n</c> inside — so Spectre can never inflate it into multiple
    /// terminal rows behind the viewport budget's back. That single-row-per-Markup contract is what
    /// lets <see cref="BuildBody"/> materialise the whole history and slice an exact window of it.
    /// </remarks>
    private List<IRenderable> RenderMessageRows(DisplayedMessage message, int termWidth)
    {
        List<IRenderable> rows = [];

        // Normalize before counting: trim leading/trailing whitespace and collapse blank-line
        // runs. Morgana authors replies for rich channels (trailing newlines, doubled blank
        // lines); Rune has no markdown renderer to absorb them and counting one row per '\n'
        // would inflate the content height with invisible phantom rows — lighting the header's
        // ▲ scroll glyph on a conversation that fits the viewport. This is the Grimoire-port
        // adjustment for a channel that carries neither rich cards nor quick replies.
        // Resolve emoji shortcodes (:white_check_mark: → ✅) to real glyphs, mirroring Grimoire's
        // renderers. Rune renders verbatim and has no Markup expansion for shortcodes, so a model
        // that emits GitHub-style codes would otherwise leave them literal on screen. Glyphs are
        // honest plain Unicode (not a "rich" capability) and Rune's rune/cell-based wrapper below
        // measures the resolved glyph correctly. Order: resolve first, then strip variation
        // selectors from the produced glyphs so the cell-width accounting stays exact.
        string text = cells.StripVariationSelectors(BlankRunRegex.Replace(Emoji.Replace(message.Text.Trim()), "\n\n"));
        // The channel's own notices are no speaker of the conversation, so they carry no dot. The dot is
        // wrapped with the row it opens, so the cell budget of that row counts it
        string? speakerMarkerColor = message.Who == channelSpeaker ? null : SpeakerColor(message.Who);
        string fullText = speakerMarkerColor is null ? $"{message.Who}: {text}" : $"{SpeakerMarker}{message.Who}: {text}";
        bool first = true;
        foreach (string line in fullText.Split('\n'))
        {
            // Wrap by terminal CELLS (not char count) on whole runes: a wide CJK glyph counts as
            // two columns and a combining mark as zero, so a char-indexed chunk could overflow the
            // row and Spectre would silently wrap it — breaking the one-Markup-per-row contract that
            // BuildBody's scrollback budget depends on.
            foreach (string chunk in cells.Wrap(line, termWidth))
            {
                EmitMessageRow(rows, message, chunk, isFirstRowOfMessage: first, speakerMarkerColor);
                first = false;
            }
        }
        return rows;
    }

    /// <summary>Renders a single pre-wrapped row of a message: the speaker's dot and a bold "Who:" prefix on the very first row, same speaker colour without bold on every other row.</summary>
    private static void EmitMessageRow(List<IRenderable> output, DisplayedMessage message, string chunk, bool isFirstRowOfMessage, string? speakerMarkerColor)
    {
        if (isFirstRowOfMessage)
        {
            // The dot takes the speaker's own colour even when the row wears a warning's: who spoke and what
            // kind of line it is read apart. It is drawn ahead of the name and is no part of the message
            string marker = string.Empty;
            if (speakerMarkerColor is not null && chunk.StartsWith(SpeakerMarker, StringComparison.Ordinal))
            {
                marker = $"[{speakerMarkerColor}]●[/] ";
                chunk = chunk[SpeakerMarker.Length..];
            }

            // First row of the message — bold the "Who:" prefix, then the rest of the
            // row in the same colour but non-bold. If the speaker name itself wraps
            // past the colon, fall back to bolding the whole chunk so the message
            // still visually starts emphasised.
            int colonIdx = chunk.IndexOf(':');
            if (colonIdx > 0 && colonIdx < chunk.Length)
            {
                string who = chunk[..colonIdx];
                string rest = chunk[(colonIdx + 1)..];
                output.Add(new Markup($"{marker}[bold {message.Color}]{Markup.Escape(who)}:[/][{message.Color}]{Markup.Escape(rest)}[/]"));
            }
            else
            {
                output.Add(new Markup($"{marker}[bold {message.Color}]{Markup.Escape(chunk)}[/]"));
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
        // A command waiting on an answer takes the prompt over entirely: until it is answered there is nothing
        // else the user can do here, a spent conversation included
        if (commandConfirmation.IsPending)
            return [.. commandConfirmation.RenderQuestion(termWidth)];

        // The form asks above the caret: the question and the value being typed have to be read together
        List<IRenderable> optionPromptRows = [.. commandOptionPrompt.RenderPrompt(termWidth)];

        // A command reporting its progress says more than the thinking hint it stands in for: it names the
        // step being worked on, so the wait is accounted for rather than merely announced. It holds the
        // prompt for as long as it runs — here or on Morgana — since a command is answered, never talked over
        if ((awaitingResponse || commandRunning) && commandProgress.IsActive)
            return [.. commandProgress.RenderProgress(termWidth)];

        // A command that has not reported yet still holds the prompt and says so: a line typed now would be lost
        if (commandRunning && !awaitingResponse)
            return ChunkStyledRows($"/{runningCommandName} is running…", termWidth, $"{CommandReportColor} italic");

        // What the last command came to sits above whatever the prompt is now, in place of the widget it follows
        List<IRenderable> outcomeRows = commandOutcome is { } outcome
            ? ChunkStyledRows(outcome, termWidth, commandOutcomeColor)
            : [];

        // Terminal state supersedes everything but a command line being typed, which is how the user gets
        // out of it. Morgana's banner already told the user the dust ran out; here they learn the way on.
        if (conversationDead && !CommandPaletteService.IsCommandLine(currentInput))
            return [.. outcomeRows, .. ChunkStyledRows("✦ Conversation spent — type /new to start a fresh one or press Esc to quit", termWidth, $"{ErrorColor} italic")];

        if (awaitingResponse)
        {
            // Tinted in the speaker's own colour (emerald for base Morgana, light green for a
            // specialised agent) instead of a flat grey, so the hint already signals who's about
            // to answer before the first word of the reply arrives.
            string content = $"{currentSpeaker} is thinking…";
            return ChunkStyledRows(content, termWidth, $"{SpeakerColor(currentSpeaker)} italic");
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

        // A line naming a command exactly takes the match colour, as in Claude Code: Enter will run what is written
        bool namesCommand = !commandOptionPrompt.IsActive && commandPalette.NamesCommandExactly(currentInput, ConversationState());
        foreach (System.Text.Rune rune in currentInput.EnumerateRunes())
        {
            string glyph = Markup.Escape(rune.ToString());
            if (namesCommand)
                glyph = $"[{commandPalette.MatchColor}]{glyph}[/]";
            if (!caretPlaced && cursorPosition >= charOffset && cursorPosition < charOffset + rune.Utf16SequenceLength)
            {
                units.Add(($"[blink {UserColor} invert]{glyph}[/]", cells.RuneCells(rune)));
                caretPlaced = true;
            }
            else
                units.Add((glyph, cells.RuneCells(rune)));
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
        return FramePromptRows([.. outcomeRows, .. optionPromptRows, .. rows], termWidth);
    }

    /// <summary>Sandwiches the free-text input row between two full-width rules in the same grey as the header panel's border, so the caret isn't the only thing marking "you type here". Not used for the thinking hint or the dead-conversation notice — those already read as "not a normal turn" on their own.</summary>
    private static List<IRenderable> FramePromptRows(List<IRenderable> rows, int termWidth)
    {
        if (rows.Count == 0)
            return rows;

        // Neutral grey keeps the prompt frame as plain chrome: the speaker colour is spoken for by
        // the header band, so the input area must not read as a second, competing accent.
        Markup border = new($"[grey50]{new string('─', Math.Max(1, termWidth))}[/]");
        return [border, .. rows, border];
    }

    /// <summary>Splits <paramref name="content"/> into <paramref name="termWidth"/>-cell chunks, each rendered as a single-row <see cref="Markup"/> wrapped in <paramref name="style"/>.</summary>
    private List<IRenderable> ChunkStyledRows(string content, int termWidth, string style)
    {
        List<IRenderable> rows = [];
        foreach (string chunk in cells.Wrap(content, termWidth))
            rows.Add(new Markup($"[{style}]{Markup.Escape(chunk)}[/]"));
        return rows;
    }

    /// <summary>UTF-16 length (1, or 2 for a surrogate pair) of the rune ending just before <paramref name="pos"/> in <see cref="currentInput"/>. Caller guarantees <paramref name="pos"/> &gt; 0; keeps the caret on a rune boundary going left.</summary>
    private int RuneLengthBefore(int pos) =>
        pos >= 2 && char.IsLowSurrogate(currentInput[pos - 1]) && char.IsHighSurrogate(currentInput[pos - 2]) ? 2 : 1;

    /// <summary>UTF-16 length (1, or 2 for a surrogate pair) of the rune starting at <paramref name="pos"/> in <see cref="currentInput"/>. Caller guarantees <paramref name="pos"/> &lt; length; keeps the caret on a rune boundary going right.</summary>
    private int RuneLengthAt(int pos) =>
        pos + 1 < currentInput.Length && char.IsHighSurrogate(currentInput[pos]) && char.IsLowSurrogate(currentInput[pos + 1]) ? 2 : 1;

    /// <summary>
    /// Cleans reply text bound for the terminal, keeping the line structure Morgana authored: newlines
    /// survive, a tab becomes a space and every other control character is dropped for the reasons given
    /// in <see cref="TerminalCellService.StripControlCharacters"/>. The breaks are load-bearing — <see cref="RenderMessageRows"/>
    /// turns each one into a row, so dropping them would glue the last word of a line to the first of the
    /// next and hand the wrapper one endless paragraph.
    /// </summary>
    private static string SanitizeMessageText(string text)
    {
        StringBuilder sanitized = new(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            switch (c)
            {
                // A reply authored on Windows breaks its lines with a pair: one break, not a break plus a stray glyph
                case '\r' when i + 1 < text.Length && text[i + 1] == '\n':
                    continue;
                case '\r':
                case '\n':
                    sanitized.Append('\n');
                    continue;
                case '\t':
                    sanitized.Append(' ');
                    continue;
                default:
                    if (!char.IsControl(c))
                        sanitized.Append(c);
                    continue;
            }
        }
        return sanitized.ToString();
    }

    /// <summary>What opens the first row of a line the user or Morgana's side said, ahead of the speaker's name. Drawn only.</summary>
    private const string SpeakerMarker = "● ";

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
    /// speaker's palette color. What a command reports never reaches the transcript. Rune has no banner widget, so color is the only signal that
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

    /// <summary>A single history row: speaker name, rendered text and the Spectre color token used for the name.</summary>
    private record DisplayedMessage(string Who, string Text, string Color);
}