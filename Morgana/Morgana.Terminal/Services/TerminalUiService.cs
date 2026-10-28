using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
using Morgana.Contracts;
using Morgana.Terminal.Interfaces;
using Morgana.Terminal.Messages;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Morgana.Terminal.Services;

/// <summary>
/// The live terminal of a TTY channel on Spectre.Console: a sticky header over a scrolling transcript with the
/// prompt at the bottom. What the channel declares in its <see cref="ChannelProfile"/> decides what it shows:
/// the streaming pane and the quick replies exist only where the profile says so.
/// </summary>
public sealed class TerminalUiService : ITerminalUi
{
    /// <summary>Fallback for the channel's <c>ReplyTimeoutSeconds</c> when absent or non-positive.</summary>
    private const int DefaultReplyTimeoutSeconds = 120;

    /// <summary>Fallback for the channel's <c>MaxInputLength</c> when absent or non-positive.</summary>
    private const int DefaultMaxInputLength = 500;

    /// <summary>Number of rows the scrollback advances per keystroke.</summary>
    private const int ScrollStep = 5;

    /// <summary>Every message ever displayed in chronological order, from which each frame derives the visible window.</summary>
    private readonly List<DisplayedMessage> history = [];

    /// <summary>The rows of the first <see cref="historyRowsMessageCount"/> messages of <see cref="history"/> wrapped at <see cref="historyRowsWidth"/>, so a frame renders only what was added since the last one.</summary>
    private readonly List<IRenderable> historyRows = [];

    /// <summary>How many messages of <see cref="history"/> are already rendered into <see cref="historyRows"/>.</summary>
    private int historyRowsMessageCount;

    /// <summary>Terminal width <see cref="historyRows"/> was wrapped at; a resize to another width renders the history again.</summary>
    private int historyRowsWidth = -1;

    /// <summary>Last terminal width a frame was drawn against, the fallback of <see cref="ReadViewport"/>; the 80x24 seed only shows if the very first frame finds no terminal.</summary>
    private int lastViewportWidth = 80;

    /// <summary>Last terminal height a frame was drawn against, the fallback of <see cref="ReadViewport"/>.</summary>
    private int lastViewportHeight = 24;

    /// <summary>Everything Morgana delivers in the order it was produced, so a final message can never overtake a chunk that precedes it.</summary>
    private readonly Channel<InboundEvent> inbound = Channel.CreateUnbounded<InboundEvent>();

    /// <summary>How long a turn may go without a sign of life before the prompt is handed back to the user.</summary>
    private readonly TimeSpan replyTimeout;

    /// <summary>Ends the countdown on the turn in flight; null when nothing is being waited for. Mutated only under <see cref="renderLock"/>.</summary>
    private CancellationTokenSource? replyWatchdog;

    /// <summary>Keystrokes not yet committed with <see cref="ConsoleKey.Enter"/>.</summary>
    private string currentInput = string.Empty;

    /// <summary>Insertion caret as an index into <see cref="currentInput"/>, always on a rune boundary. Mutated only under <see cref="renderLock"/>.</summary>
    private int cursorPosition;

    /// <summary>Hard cap on the prompt length in UTF-16 chars: typing past it is swallowed while the caret keys and deletions stay live.</summary>
    private readonly int maxInputLength;

    /// <summary>The speaker the header names; an agent's name mid-turn and <c>Morgana</c> again on completion.</summary>
    private string currentSpeaker = TerminalTheme.MorganaSpeaker;

    /// <summary>The conversation on screen, shown truncated in the header.</summary>
    private string conversationId = string.Empty;

    /// <summary>The dust gauge as Spectre markup, empty while Morgana reports no dust level. Volatile so the header reads it without the lock.</summary>
    private volatile string dustGaugeMarkup = string.Empty;

    /// <summary>The dust level Morgana last reported as a fraction, null while none was. Mutated only under <see cref="renderLock"/>.</summary>
    private double? reportedDustLevel;

    /// <summary>Set once the user types <c>/exit</c> or presses <see cref="ConsoleKey.Escape"/>.</summary>
    private volatile bool exitRequested;

    /// <summary>True while a turn is in flight: keystrokes except Esc are swallowed and the prompt shows a thinking hint. Starts true since the channel always waits for Morgana's presentation first.</summary>
    private volatile bool awaitingResponse = true;

    /// <summary>The agent carrying the conversation, null while Morgana holds it herself. Kept apart from the speaker the header shows: a display choice must never decide which commands exist.</summary>
    private volatile string? agentCarryingConversation;

    /// <summary>Latched when Morgana delivers the dust-exhaustion notice: the conversation is spent and only the ways out of it are accepted until another conversation replaces it. Volatile so the key loop reads it without the lock.</summary>
    private volatile bool conversationDead;

    /// <summary>How many rows above the live bottom the viewport is anchored; non-zero only while the conversation is at rest. Mutated and read only under <see cref="renderLock"/>.</summary>
    private int scrollOffset;

    /// <summary>Whether older content exists above the visible window, which lights the header's up arrow. Set by <see cref="BuildBody"/>.</summary>
    private bool scrollHasAbove;

    /// <summary>Whether content exists below the visible window, which lights the header's down arrow. Set by <see cref="BuildBody"/>.</summary>
    private bool scrollHasBelow;

    /// <summary>Tells when the terminal is resized, so the viewport follows the window without polling on every keystroke.</summary>
    private readonly IViewportResizeWatcher viewportResizeWatcher;

    /// <summary>Wraps and truncates by terminal cell, so a wide glyph never desyncs the row budget of <see cref="BuildBody"/>.</summary>
    private readonly TerminalCellService cells;

    /// <summary>Draws the transcript's messages in the channel's own way.</summary>
    private readonly IMessageRenderer messageRenderer;

    /// <summary>The options of a reply as the prompt of its turn. Offered only where the profile declares quick replies.</summary>
    private readonly QuickReplyPromptService quickReplyPrompt;

    /// <summary>The typewriter that reveals a streamed reply. Fed only where the profile declares streaming.</summary>
    private readonly StreamingPaneService streamingPane;

    /// <summary>The dropdown of commands shown under the prompt while the line starts with a slash. Called only under <see cref="renderLock"/>.</summary>
    private readonly CommandPaletteService commandPalette;

    /// <summary>The Yes/No question that a command which cannot be taken back must have answered first. Called only under <see cref="renderLock"/>.</summary>
    private readonly CommandConfirmationService commandConfirmation;

    /// <summary>The bar a running command reports itself through. Called only under <see cref="renderLock"/>.</summary>
    private readonly CommandProgressService commandProgress;

    /// <summary>The form asking for the values a command declares. Called only under <see cref="renderLock"/>.</summary>
    private readonly CommandOptionPromptService commandOptionPrompt;

    /// <summary>Runs the command the palette resolved.</summary>
    private readonly TerminalCommandRegistryService commandRegistry;

    /// <summary>Who the channel is and what it declares it can show.</summary>
    private readonly ChannelProfile profile;

    /// <summary>The colours and the progress vocabulary of the channel.</summary>
    private readonly TerminalTheme theme;

    /// <summary>True while a command is running, whether it works here or on Morgana: a command that opens no turn has nothing else telling the screen it is busy. Volatile since the task running the command sets it while a frame is drawn.</summary>
    private volatile bool commandRunning;

    /// <summary>The command running now, named by the hint that holds the prompt until its first frame. Under <see cref="renderLock"/>.</summary>
    private string runningCommandName = string.Empty;

    /// <summary>Why the command line just typed ran nothing, drawn above the prompt and kept out of the transcript. Null once the user writes, starts a turn or runs another command. Under <see cref="renderLock"/>.</summary>
    private string? promptNotice;

    /// <summary>The colour <see cref="promptNotice"/> is drawn in.</summary>
    private string promptNoticeColor = TerminalTheme.WarningColor;

    /// <summary>The panel a command opened over the whole body, laid out again at every frame for the width then; null when none is open. Under <see cref="renderLock"/>.</summary>
    private Func<int, IReadOnlyList<Markup>>? commandPanel;

    /// <summary>The last command run started, until its outcome is shown: a late outcome of an earlier run never covers the current one's. Under <see cref="renderLock"/>.</summary>
    private CommandInvocation? invocationOwingOutcome;

    /// <summary>The session of the running UI, whose conversation receives what the user types. Set when the UI starts.</summary>
    private TerminalSessionService? startedSession;

    /// <summary>The live display of the running UI, which commands' requests repaint. Set once the display starts.</summary>
    private LiveDisplayContext? startedDisplay;

    /// <summary>Fires when the host stops, so a deadline armed by a turn or a command dies with the process.</summary>
    private CancellationToken uiStopping;

    /// <summary>Called off when the user leaves, so a command still waiting on Morgana never holds the exit.</summary>
    private CancellationTokenSource commandCancellation = new();

    /// <summary>Completes when deliveries may be drawn. Pending while another conversation is being opened, so its presentation cannot land on the transcript about to be cleared.</summary>
    private Task deliveriesReleased = Task.CompletedTask;

    /// <summary>Serializes the mutations of the transcript and the prompt together with the paired layout update and refresh, since the resize callback and the timers run on their own threads.</summary>
    private readonly Lock renderLock = new();

    /// <summary>Reads the reply deadline and the input cap, then captures the collaborators and the channel's profile.</summary>
    public TerminalUiService(
        IConfiguration configuration,
        IViewportResizeWatcher viewportResizeWatcher,
        TerminalCellService cells,
        IMessageRenderer messageRenderer,
        QuickReplyPromptService quickReplyPrompt,
        StreamingPaneService streamingPane,
        CommandPaletteService commandPalette,
        CommandConfirmationService commandConfirmation,
        CommandProgressService commandProgress,
        CommandOptionPromptService commandOptionPrompt,
        TerminalCommandRegistryService commandRegistry,
        ChannelProfile profile,
        TerminalTheme theme)
    {
        // The collaborators every surface of the UI is drawn and decided with
        this.viewportResizeWatcher = viewportResizeWatcher;
        this.cells = cells;
        this.messageRenderer = messageRenderer;
        this.quickReplyPrompt = quickReplyPrompt;
        this.streamingPane = streamingPane;
        this.commandPalette = commandPalette;
        this.commandConfirmation = commandConfirmation;
        this.commandProgress = commandProgress;
        this.commandOptionPrompt = commandOptionPrompt;
        this.commandRegistry = commandRegistry;
        this.profile = profile;
        this.theme = theme;

        // A non-positive wait would declare every turn lost the instant it is sent, so it falls back too
        int replyTimeoutSeconds = configuration.GetValue<int?>(profile.SectionKey("ReplyTimeoutSeconds")) ?? DefaultReplyTimeoutSeconds;
        replyTimeout = TimeSpan.FromSeconds(replyTimeoutSeconds > 0 ? replyTimeoutSeconds : DefaultReplyTimeoutSeconds);

        // A non-positive cap would lock the prompt shut, so it falls back as well
        maxInputLength = configuration.GetValue<int?>(profile.SectionKey("MaxInputLength")) ?? DefaultMaxInputLength;
        if (maxInputLength <= 0)
            maxInputLength = DefaultMaxInputLength;
    }

    /// <summary>The session of the running UI.</summary>
    /// <exception cref="InvalidOperationException">Thrown before the UI has started.</exception>
    private TerminalSessionService Session => startedSession ?? throw new InvalidOperationException("The terminal UI has not started.");

    /// <summary>The live display of the running UI.</summary>
    /// <exception cref="InvalidOperationException">Thrown before the display has started.</exception>
    private LiveDisplayContext LiveDisplay => startedDisplay ?? throw new InvalidOperationException("The live display has not started.");

    /// <inheritdoc />
    public void EnqueueIncoming(ChannelMessage message) => inbound.Writer.TryWrite(new MessageEvent(message));

    /// <inheritdoc />
    public Action<StreamChunkRequest>? ChunkSink => profile.Capabilities.SupportsStreaming
        ? chunk => inbound.Writer.TryWrite(new ChunkEvent(chunk.ConversationId, chunk.ChunkText))
        : null;

    /// <inheritdoc />
    public async Task RunAsync(TerminalSessionService session, CancellationToken cancellationToken = default)
    {
        // The first frame is laid out under the lock like every later one, so all fields it reads are always accessed under it
        Layout layout;
        lock (renderLock)
        {
            // The session outlives any one conversation; the header shows the one open as the UI starts
            startedSession = session;
            conversationId = session.ConversationId;
            layout = BuildLayout();
        }

        // Spectre never touches the terminal's own cursor, which would otherwise sit wherever the last partial
        // repaint left it as a stray blinking block. The prompt draws its own caret, so the cursor stays hidden
        // for the whole session and is given back for the user's shell
        AnsiConsole.Cursor.Hide();
        try
        {
            // The display lives until the user leaves or the host stops
            await RunLiveDisplayAsync(layout, cancellationToken);
        }
        finally
        {
            // The user's shell gets its cursor back whatever way the display ended
            AnsiConsole.Cursor.Show();
        }
    }

    /// <summary>Runs the live display until the user leaves or the host stops, reading keys and draining Morgana's deliveries beside each other.</summary>
    private async Task RunLiveDisplayAsync(Layout layout, CancellationToken cancellationToken)
    {
        await AnsiConsole
            .Live(layout)
            .AutoClear(false)
            .Overflow(VerticalOverflow.Ellipsis)
            .Cropping(VerticalOverflowCropping.Top)
            .StartAsync(async ctx =>
            {
                // The viewport follows the window for as long as the display lives, whether the UI ends by
                // quit, Esc, cancellation or a dead terminal
                using IDisposable resizeSubscription = viewportResizeWatcher.Subscribe(() =>
                {
                    // The resize repaints under the lock like any other change of the frame
                    lock (renderLock)
                        Repaint(ctx);
                });

                // The opening frame is painted at once: a backend too slow for the startup wait, or unable to
                // reach the callback, would otherwise leave a blank terminal with no sign the channel is waiting
                lock (renderLock)
                    ctx.Refresh();

                // The host's stopping token arms the deadlines and the display is what commands repaint
                uiStopping = cancellationToken;
                startedDisplay = ctx;

                // Commands die with the process as well as with the user leaving
                commandCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

                // The typewriter repaints and commits the held finals through this UI, under its lock
                streamingPane.Start(renderLock, () => Repaint(ctx), message => CommitFinalMessage(ctx, message));

                // Keys and deliveries are served beside each other
                Task readLoop = Task.Run(() => ReadKeysLoop(ctx, cancellationToken), cancellationToken);
                Task drainLoop = Task.Run(() => DrainInboundLoop(ctx, cancellationToken), cancellationToken);
                try
                {
                    // The first loop to end ends the display
                    await Task.WhenAny(readLoop, drainLoop);
                }
                finally
                {
                    // A typewriter still ticking would call back into a display that is gone
                    lock (renderLock)
                        streamingPane.Reset();
                }
            });
    }

    /// <summary>Consumes the inbound queue strictly in order, routing each event to the handler of its kind.</summary>
    private async Task DrainInboundLoop(LiveDisplayContext ctx, CancellationToken cancellationToken)
    {
        try
        {
            // Deliveries are taken one at a time in the order Morgana produced them
            await foreach (InboundEvent inboundEvent in inbound.Reader.ReadAllAsync(cancellationToken))
            {
                // A conversation being swapped in holds its deliveries until the screen has been cleared for it,
                // then whatever the conversation left behind still queued is dropped
                await Volatile.Read(ref deliveriesReleased);
                if (!Session.IsConversationOnScreen(inboundEvent.ConversationId))
                    continue;

                // The queue holds only messages and chunks, so no other kind of event can reach here
                switch (inboundEvent)
                {
                    // A chunk feeds the typewriter
                    case ChunkEvent { ChunkText: string chunkText }:
                        HandleInboundChunk(ctx, chunkText);
                        break;

                    // A frame of a command run on Morgana is chrome: the bar holds the prompt while the command
                    // works, then the finished frame's text becomes its outcome. Nothing of it reaches the transcript
                    case MessageEvent { Message.Progress: not null } progressEvent:
                        HandleInboundProgress(ctx, progressEvent.Message);
                        break;

                    // A command's outcome always travels on its finished frame: a command line naming no command
                    // belongs to nothing on screen and has no place in the transcript either
                    case MessageEvent { Message.MessageType: ChannelMessageTypes.System }:
                        break;

                    // Any other message is a turn of the conversation; an exit requested mid-turn is honoured
                    // only after the last frame is painted, so the user sees Morgana's final reply first
                    case MessageEvent { Message: ChannelMessage message }:
                        if (HandleInboundMessage(ctx, message))
                            return;
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Cancellation is the normal way out of the drain
        }
    }

    /// <summary>Gives a chunk to the typewriter and re-arms the reply deadline, since a chunk is Morgana answering.</summary>
    private void HandleInboundChunk(LiveDisplayContext ctx, string chunkText)
    {
        // An empty chunk reveals nothing
        if (chunkText.Length == 0)
            return;

        // The pane and the deadline are shared with the timer thread
        lock (renderLock)
        {
            // The chunk joins the text waiting to be revealed
            streamingPane.Append(chunkText);

            // Silence is what the deadline measures; it starts over with every chunk
            if (awaitingResponse)
                StartReplyWatchdog(ctx);
        }
    }

    /// <summary>
    /// Commits a final message to the transcript now or, while the typewriter is still revealing, once it has
    /// finished. Returns true when an exit was requested so the drain loop stops after the last frame.
    /// </summary>
    private bool HandleInboundMessage(LiveDisplayContext ctx, ChannelMessage message)
    {
        lock (renderLock)
        {
            // Morgana has spoken, so the turn is no longer at risk of being declared lost: on arrival, not on
            // commit, since the typewriter may hold the text for a while yet
            CancelReplyWatchdog();

            // The reveal commits it when it ends, so the transcript never snaps ahead of what the user has read
            if (streamingPane.TryHoldFinal(message))
                return exitRequested;

            // Nothing is being revealed: the message goes straight to the transcript
            CommitFinalMessage(ctx, message);
            return exitRequested;
        }
    }

    /// <summary>
    /// Commits a final message to the transcript and takes from it everything the screen shows: who holds the
    /// conversation, the dust gauge, a spent budget and the quick replies. Must be called under <see cref="renderLock"/>.
    /// </summary>
    private void CommitFinalMessage(LiveDisplayContext ctx, ChannelMessage message)
    {
        // The row belongs to whoever authored it, so a specialised agent keeps its colour even on the farewell
        // line that carries AgentCompleted. The name comes over the unauthenticated callback, so it is cleaned
        // once here and every place that shows it afterwards reuses this value
        string messageSpeaker = string.IsNullOrWhiteSpace(message.AgentName)
            ? TerminalTheme.MorganaSpeaker
            : TerminalCellService.StripControlCharacters(message.AgentName);

        // A card is kept only where the profile declares rich cards: any other channel must never gain one
        history.Add(new DisplayedMessage(
            messageSpeaker,
            message.Text,
            RowColor(message, messageSpeaker),
            message.MessageType,
            profile.Capabilities.SupportsRichCards ? message.RichCard : null));

        // The header reverts to Morgana on completion, so the next user turn is not drawn under the outgoing agent's colour
        currentSpeaker = message.AgentCompleted || string.IsNullOrWhiteSpace(message.AgentName)
            ? TerminalTheme.MorganaSpeaker
            : messageSpeaker;

        // An agent that signalled completion has handed the conversation back, so nothing addressed at the
        // agent carrying it applies any more; a reply from Morgana says the same
        agentCarryingConversation = message.AgentCompleted || !IsSpecializedAgent(message.AgentName)
            ? null
            : messageSpeaker;

        // Any message carrying metadata refreshes the gauge: the reply carries the estimate made before delivery and
        // a trailing warning in the same turn carries the authoritative level after the channel adapter has spent its dust
        if (message.ConversationMetadata?.DustLevel is { } level)
        {
            // Truncated rather than rounded: a sub-1% residual reads as 0%, the slack that funds the channel's
            // presentation messages and the let-it-finish turn
            int dustPercent = Math.Clamp((int)(level * 100), 0, 100);

            // The colour follows the depletion with Cauldron's dust meter thresholds
            string dustColor = level > 0.30 ? theme.MorganaColor : level > 0.10 ? TerminalTheme.DustLowColor : TerminalTheme.DustCriticalColor;
            dustGaugeMarkup = $"   [grey54]dust[/] [bold {dustColor}]{dustPercent}%[/]";
            reportedDustLevel = level;
        }

        // Morgana pushes the exhaustion notice at the end of the turn, so the user learns of it before wasting a
        // keystroke. The red banner above stays Morgana's word and the prompt shows the way on: /new or Esc
        if (string.Equals(message.ErrorReason, ChannelErrorReasons.DustBudgetExhausted, StringComparison.Ordinal))
            MarkConversationSpent();

        // Where the profile declares quick replies the offered options become the prompt of this turn, as Cauldron
        // locks its textarea while they are pending. A spent conversation has nothing left to branch into
        if (profile.Capabilities.SupportsQuickReplies && !conversationDead && message.QuickReplies is { Count: > 0 } quickReplies)
            quickReplyPrompt.Offer(quickReplies);

        // The input gate opens: the key loop swallowed keystrokes until this delivery landed
        ReleaseTurn();
        Repaint(ctx);
    }

    /// <summary>Polls the keyboard every 25 ms and gives each key to the surface that owns it at that moment: panel, confirmation, form, palette, scrollback, quick replies or the prompt.</summary>
    private async Task ReadKeysLoop(LiveDisplayContext ctx, CancellationToken cancellationToken)
    {
        while (!exitRequested && !cancellationToken.IsCancellationRequested)
        {
            // Polled rather than blocking: Spectre's live rendering cannot share stdin with a first-class prompt.
            // A terminal that died without a signal makes the next stdin operation throw, which is treated as an
            // exit so the process ends and docker's --rm reclaims the container
            ConsoleKeyInfo key;
            try
            {
                // No key is waiting: the loop sleeps one poll interval
                if (!Console.KeyAvailable)
                {
                    // The sleep ends early when the host stops
                    await Task.Delay(25, cancellationToken);
                    continue;
                }

                // A key is waiting: it is read without echo since the prompt draws it
                key = Console.ReadKey(intercept: true);
            }
            catch (OperationCanceledException)
            {
                // Shutdown was asked for: closing the delivery stream returns the drain loop as well.
                // The exit flag stays untouched since nobody typed /exit
                inbound.Writer.TryComplete();
                return;
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                // A dead terminal ends the session
                exitRequested = true;
                inbound.Writer.TryComplete();
                return;
            }

            // An open panel owns the keyboard until it is closed, so a key meant for it never reaches the prompt beneath
            if (TryHandlePanelKey(ctx, key.Key))
                continue;

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

            // Scrollback reviews the finished conversation: never while quick replies are the prompt, which must be
            // answered first, nor while a turn is in flight. It comes before the swallow gate so it still works on a spent conversation
            if (!quickReplyPrompt.IsOffering && !awaitingResponse && TryScrollDelta(key.Key, out int scrollDelta))
            {
                ApplyScroll(ctx, scrollDelta);
                continue;
            }

            // Everything but Esc is swallowed while Morgana is to speak and for good once the conversation is spent,
            // except a command line, the only way out of it short of Esc. A running command holds the prompt as a turn
            // does. The flags are volatile: taking the lock for every polled key would only contend with the drain loop
            if ((awaitingResponse || commandRunning || (conversationDead && !AcceptsKeyOnSpentConversation(key))) && key.Key != ConsoleKey.Escape)
                continue;

            // Quick replies own the keyboard: the options are the prompt, so the arrows move the highlight and Enter sends
            // the pick. Esc falls through to exit. A slash is the one exception since commands are not prose: it opens a
            // command line over the options, which come back once that line is dismissed or emptied
            if (quickReplyPrompt.IsOffering && key.Key != ConsoleKey.Escape && !IsCommandLineOpen())
            {
                if (key.KeyChar == '/')
                    OpenCommandLine(ctx);
                else
                    await HandleQuickReplyKeyAsync(ctx, key.Key);
                continue;
            }

            // The keys the prompt does not own fall to the default case
            switch (key.Key)
            {
                // Enter commits whatever the prompt holds
                case ConsoleKey.Enter:
                    await CommitLineAsync(ctx);
                    break;

                // Backspace deletes the rune left of the caret, which is the tail only at the end of the line
                case ConsoleKey.Backspace:
                    lock (renderLock)
                    {
                        // At the start of the line there is nothing to delete
                        if (cursorPosition > 0)
                        {
                            // A surrogate pair is deleted whole
                            int runeLength = RuneLengthBefore(cursorPosition);
                            currentInput = currentInput.Remove(cursorPosition - runeLength, runeLength);
                            cursorPosition -= runeLength;
                            promptNotice = null;
                            RefreshWhenInputSettles(ctx);
                        }
                    }
                    break;

                // Delete removes the rune under the caret and leaves the caret put
                case ConsoleKey.Delete:
                    lock (renderLock)
                    {
                        // At the end of the line there is nothing under the caret
                        if (cursorPosition < currentInput.Length)
                        {
                            // A surrogate pair is deleted whole
                            int runeLength = RuneLengthAt(cursorPosition);
                            currentInput = currentInput.Remove(cursorPosition, runeLength);
                            promptNotice = null;
                            RefreshWhenInputSettles(ctx);
                        }
                    }
                    break;

                // Left steps the caret back over one whole rune
                case ConsoleKey.LeftArrow:
                    lock (renderLock)
                    {
                        // At the start of the line the caret cannot move left
                        if (cursorPosition > 0)
                        {
                            // The caret steps over a whole rune
                            cursorPosition -= RuneLengthBefore(cursorPosition);
                            RefreshWhenInputSettles(ctx);
                        }
                    }
                    break;

                // Right steps the caret forward over one whole rune
                case ConsoleKey.RightArrow:
                    lock (renderLock)
                    {
                        // At the end of the line the caret cannot move right
                        if (cursorPosition < currentInput.Length)
                        {
                            // The caret steps over a whole rune
                            cursorPosition += RuneLengthAt(cursorPosition);
                            RefreshWhenInputSettles(ctx);
                        }
                    }
                    break;

                // Esc outside every command surface leaves; completing the queue lets the drain loop return
                case ConsoleKey.Escape:
                    RequestExit();
                    return;

                // Any other key feeds the prompt when it is a printable glyph; arrows and function keys are ignored
                default:
                    if (!char.IsControl(key.KeyChar))
                    {
                        // The buffer is shared with the drain loop
                        lock (renderLock)
                        {
                            // Once the buffer is full further glyphs are swallowed while caret moves and deletions stay
                            // live; the header counter explains the stop
                            if (currentInput.Length < maxInputLength)
                            {
                                // Typing splices in at the caret rather than appending
                                currentInput = currentInput.Insert(cursorPosition, key.KeyChar.ToString());
                                cursorPosition++;

                                // The user has moved on from the last command: its outcome makes room for what is typed
                                promptNotice = null;
                                RefreshWhenInputSettles(ctx);
                            }
                        }
                    }
                    break;
            }
        }
    }

    /// <summary>Commits the prompt on Enter: takes the line off the prompt, then runs it as the answer to a form, a command or a turn.</summary>
    private async Task CommitLineAsync(LiveDisplayContext ctx)
    {
        SubmittedLine line = TakeSubmittedLine();

        // The form has everything it asked for and runs the command, or it is still asking: both are drawn by it
        if (line.WasFillingIn)
        {
            if (line.FormInvocation is { } invocation)
                _ = RunFilledCommandAsync(ctx, invocation);
            else
                RefreshLayout(ctx);
            return;
        }

        // An empty line commits nothing
        if (line.Text.Length == 0)
            return;

        // A command line never reaches Morgana as prose: it runs a command or is refused here.
        // The command runs beside the key loop, so Esc is still read while it waits on Morgana
        if (CommandPaletteService.IsCommandLine(line.Text))
        {
            _ = RunCommandLineAsync(ctx, line.Text, line.Command);
            return;
        }

        // Prose on a spent conversation would only be refused by Morgana and pending quick replies take none:
        // a line edited out of command mode is dropped with a notice saying why
        if (conversationDead || quickReplyPrompt.IsOffering)
        {
            ShowSystemNotice(ctx, conversationDead
                ? "The conversation is spent: type /new to start a fresh one"
                : "Choose one of the quick replies or type / for commands", TerminalTheme.WarningColor);
            return;
        }

        // Anything else is a turn of the conversation
        await SubmitTurnAsync(ctx, line.Text, () => Session.SendUserTurnAsync(line.Text));
    }

    /// <summary>
    /// Takes the line off the prompt and clears the prompt before anything is awaited, so a keystroke arriving
    /// during the send lands on a fresh buffer. The command is resolved against the line first, since the
    /// palette's highlight belongs to that line.
    /// </summary>
    private SubmittedLine TakeSubmittedLine()
    {
        lock (renderLock)
        {
            // The line as it stands is what is committed
            string text = currentInput;
            CommandDescriptor? command = null;
            CommandInvocation? formInvocation = null;

            // A command being filled in owns the line: what was typed answers the option on screen
            // rather than opening a turn or a new command
            bool wasFillingIn = commandOptionPrompt.IsActive;
            if (wasFillingIn)
                formInvocation = commandOptionPrompt.Accept(text);
            else if (CommandPaletteService.IsCommandLine(text))
                command = commandPalette.ResolveCommandToRun(text, ConversationState());

            // The line is gone from the prompt whatever becomes of it
            currentInput = string.Empty;
            cursorPosition = 0;

            // The next question opens on that option's default, ready to be taken as it stands
            if (wasFillingIn && formInvocation is null)
            {
                // The caret waits at the end of the default, ready for it to be edited
                currentInput = commandOptionPrompt.CurrentOptionDefault;
                cursorPosition = currentInput.Length;
            }

            // The line with what it resolved to
            return new SubmittedLine(text, command, wasFillingIn, formInvocation);
        }
    }

    /// <summary>
    /// Answers a key while quick replies are the prompt: a pick is echoed and sent as a turn, anything else
    /// only moves the highlight. Cauldron sends the reply's value rather than its label; this does too.
    /// </summary>
    private async Task HandleQuickReplyKeyAsync(LiveDisplayContext ctx, ConsoleKey key)
    {
        QuickReply? pickedReply;
        lock (renderLock)
        {
            // The key either moves the highlight or picks a reply
            pickedReply = quickReplyPrompt.HandleKey(key);

            // A pick is repainted by the turn it starts; a move shows its new highlight now
            if (pickedReply is null)
                Repaint(ctx);
        }

        // Nothing was picked
        if (pickedReply is null)
            return;

        // The pick is a turn like any other and gets the same deadline and the same recovery
        await SubmitTurnAsync(ctx, pickedReply.Value, () => Session.SendUserTurnAsync(pickedReply.Value));
    }

    /// <summary>Gives <paramref name="key"/> to the question a command is waiting on; false when no command is.</summary>
    private bool TryHandleConfirmationKey(LiveDisplayContext ctx, ConsoleKeyInfo key)
    {
        CommandInvocation? invocation;
        ConfirmationOutcome outcome;
        lock (renderLock)
        {
            // With no question on screen the key is not for it
            if (!commandConfirmation.IsPending)
                return false;

            // Which command the answer belongs to is only readable while the question is still open
            invocation = commandConfirmation.PendingInvocation;
            outcome = commandConfirmation.HandleKey(key);
            Repaint(ctx);
        }

        // A question that closed before it could be read leaves nothing to run
        if (invocation is null)
            return true;

        // A declined command leaves nothing behind: the prompt coming back is the answer the user just gave.
        // A confirmed one runs beside the key loop, so Esc is still read while it waits on Morgana
        if (outcome == ConfirmationOutcome.Confirmed)
            _ = RunCommandAsync(ctx, invocation, confirmed: true);
        return true;
    }

    /// <summary>Drops the command being filled in, with whatever it had gathered; false when no form is open.</summary>
    private bool TryCancelOptionPrompt(LiveDisplayContext ctx)
    {
        lock (renderLock)
        {
            // With no form open the key is not for it
            if (!commandOptionPrompt.IsActive)
                return false;

            // The form goes and the empty prompt comes back, which is all the user asked of Esc
            commandOptionPrompt.Cancel();
            currentInput = string.Empty;
            cursorPosition = 0;
            Repaint(ctx);
        }

        // The key was the form's
        return true;
    }

    /// <summary>Gives the arrows, Tab and Esc to the palette while a command line is typed at rest; false leaves the key to the loop.</summary>
    private bool TryHandleCommandPaletteKey(LiveDisplayContext ctx, ConsoleKey key)
    {
        if (key is not (ConsoleKey.UpArrow or ConsoleKey.DownArrow or ConsoleKey.Tab or ConsoleKey.Escape))
            return false;

        // The highlight is shared with the render path
        lock (renderLock)
        {
            // No palette while Morgana is answering or outside a command line: the arrows scroll and Esc leaves.
            // A form asking for a value is not a command line either, whatever the value happens to start with:
            // a path opens with a slash and must not turn the prompt into a command picker
            if (awaitingResponse || commandRunning || commandOptionPrompt.IsActive || !CommandPaletteService.IsCommandLine(currentInput))
                return false;

            // Only the four keys the caller lets through reach here
            switch (key)
            {
                // Up walks towards the best match, wrapping at both ends
                case ConsoleKey.UpArrow:
                    commandPalette.MoveHighlight(currentInput, ConversationState(), -1);
                    break;

                // Down walks away from the best match, wrapping at both ends
                case ConsoleKey.DownArrow:
                    commandPalette.MoveHighlight(currentInput, ConversationState(), 1);
                    break;

                // Tab completes the highlighted name and the caret follows to its end, ready for Enter
                case ConsoleKey.Tab:
                    if (commandPalette.CompleteHighlightedCommand(currentInput, ConversationState()) is { } completedLine)
                    {
                        // The line becomes the completed one
                        currentInput = completedLine;
                        cursorPosition = completedLine.Length;
                    }
                    break;

                // Dismissing the palette drops the line with it; a second Esc, now outside it, leaves the channel
                case ConsoleKey.Escape:
                    currentInput = string.Empty;
                    cursorPosition = 0;
                    break;
            }

            // The new highlight or line is shown
            Repaint(ctx);
            return true;
        }
    }

    /// <summary>Tells whether a spent conversation lets <paramref name="key"/> through: a slash on an empty prompt, anything once a line is started.</summary>
    private bool AcceptsKeyOnSpentConversation(ConsoleKeyInfo key)
    {
        // A line already started must stay editable and runnable, even if the caret wandered before its slash;
        // a form stays answerable even once its value has been cleared
        lock (renderLock)
            return commandOptionPrompt.IsActive || currentInput.Length > 0 || key.KeyChar == '/';
    }

    /// <summary>Tells whether a command is being written, as a line or as a form answering its options, which suspends quick replies and the spent-conversation prompt while it lasts.</summary>
    private bool IsCommandLineOpen()
    {
        // A form's value is whatever the option takes, a word such as text included, so the form itself is what counts
        lock (renderLock)
            return commandOptionPrompt.IsActive || CommandPaletteService.IsCommandLine(currentInput);
    }

    /// <summary>Starts a command line from the quick-reply picker, as typing a slash on an empty prompt does.</summary>
    private void OpenCommandLine(LiveDisplayContext ctx)
    {
        lock (renderLock)
        {
            // The slash is the whole line: the palette opens on every command and the options step aside
            currentInput = "/";
            cursorPosition = 1;
            Repaint(ctx);
        }
    }

    /// <summary>Runs the command resolved from <paramref name="line"/>; a missing one or one given values it cannot take is explained above the prompt.</summary>
    private async Task RunCommandLineAsync(LiveDisplayContext ctx, string line, CommandDescriptor? command)
    {
        // The line named no command that is available
        if (command is null)
        {
            // On a spent conversation the likely mistake is reaching for a command that needs Morgana
            ShowPromptNotice(ctx, conversationDead
                ? $"{line.Trim()} is not available on a spent conversation: type /new to start a fresh one"
                : $"{line.Trim()} is not a command: pick one with ↑↓ or type the start of its name", TerminalTheme.WarningColor);
            return;
        }

        // The values are read off the line the user typed, then judged by what the command says it takes:
        // a line that cannot be read runs nothing, since the missing half would be guessed at
        IReadOnlyDictionary<string, string> options = CommandLineParser.ParseOptions(line, out string? lineProblem);
        if (lineProblem is { } malformedLine)
        {
            // A line that cannot be read is explained and runs nothing
            ShowPromptNotice(ctx, malformedLine, TerminalTheme.WarningColor);
            return;
        }

        // A command run without what it declares asks for it: the description it publishes is exactly the
        // question to put on screen, so the user is walked through instead of being sent back to the line
        if (CommandOptionPromptService.NeedsFillingIn(command, options))
        {
            // The form opens in the same frame that shows its first question
            lock (renderLock)
            {
                // The form starts with the values already typed
                commandOptionPrompt.Begin(command, options);

                // The first question opens on that option's default, which is what running it blind would use
                currentInput = commandOptionPrompt.CurrentOptionDefault;
                cursorPosition = currentInput.Length;
                Repaint(ctx);
            }
            // The first question is on screen
            return;
        }

        // Values the command declares but cannot take are explained rather than sent
        if (command.DescribeOptionProblem(options) is { } problem)
        {
            ShowPromptNotice(ctx, problem, TerminalTheme.WarningColor);
            return;
        }

        // The values are valid: this is what would run
        CommandInvocation invocation = new(command, options);

        // A command that cannot be taken back puts its question on the prompt; whether it ever runs is the answer's business
        if (command.RequiresConfirmation)
        {
            AskForConfirmation(ctx, invocation);
            return;
        }

        // Any other command runs at once
        await RunCommandAsync(ctx, invocation, confirmed: false);
    }

    /// <summary>Runs what the form gathered, asking for confirmation first when the command wants one.</summary>
    private async Task RunFilledCommandAsync(LiveDisplayContext ctx, CommandInvocation invocation)
    {
        // The command cannot be taken back: the question goes first
        if (invocation.Command.RequiresConfirmation)
        {
            AskForConfirmation(ctx, invocation);
            return;
        }

        // Any other command runs at once
        await RunCommandAsync(ctx, invocation, confirmed: false);
    }

    /// <summary>Repaints the screen for a change the caller has already made.</summary>
    private void RefreshLayout(LiveDisplayContext ctx)
    {
        lock (renderLock)
            Repaint(ctx);
    }

    /// <summary>Repaints the screen. Must be called under <see cref="renderLock"/>, which keeps the layout update and the refresh together.</summary>
    private void Repaint(LiveDisplayContext ctx)
    {
        ctx.UpdateTarget(BuildLayout());
        ctx.Refresh();
    }

    /// <summary>Puts <paramref name="invocation"/>'s Yes/No question on the prompt, where it stays until it is answered.</summary>
    private void AskForConfirmation(LiveDisplayContext ctx, CommandInvocation invocation)
    {
        lock (renderLock)
        {
            // The question replaces the prompt with the highlight on No
            commandConfirmation.Ask(invocation);
            Repaint(ctx);
        }
    }

    /// <summary>Tells whether <paramref name="frame"/> belongs to the run still owed an outcome.</summary>
    private bool BelongsToRunOwingOutcome(CommandProgress frame) =>
        invocationOwingOutcome is { } run
        && (frame.InvocationId is { } invocationId
            ? string.Equals(invocationId, run.Id, StringComparison.Ordinal)

            // A Morgana that hands back no run id names only the command, which is then all there is to match on
            : string.Equals(frame.Command, run.Command.Name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Runs <paramref name="invocation"/>, carrying the <paramref name="confirmed"/> answer it asked for, while the prompt waits on it; a failure is explained in a panel.</summary>
    private async Task RunCommandAsync(LiveDisplayContext ctx, CommandInvocation invocation, bool confirmed)
    {
        // Two commands can never both pass the gate below, since the check and the claim happen under one lock
        lock (renderLock)
        {
            // One command at a time: a second one would race the first for the widget and the outcome line
            if (commandRunning)
                return;
            commandRunning = true;
            runningCommandName = invocation.Command.Name;
            invocationOwingOutcome = invocation;

            // The previous command's outcome gives way to the hint naming this one, as does a bar an earlier
            // command left behind when its call was given up on
            promptNotice = null;
            commandProgress.Clear();
            Repaint(ctx);
        }

        try
        {
            // The command decides what happens next through this UI: leave, report or swap the conversation
            await commandRegistry.ExecuteCommandAsync(invocation, this, confirmed, commandCancellation.Token);
        }
        catch (OperationCanceledException) when (commandCancellation.IsCancellationRequested)
        {
            // The channel is shutting down: the command dies with it
        }
        catch (TimeoutException ex)
        {
            // The panel's title names the command and the deadline's message says it was called off. An outcome that
            // landed just before it is the truth and stays: Morgana had finished the work
            lock (renderLock)
            {
                // Only the run still owed an outcome is called off on screen, and not after the user left
                if (ReferenceEquals(invocationOwingOutcome, invocation) && !exitRequested)
                {
                    // The deadline's message becomes what the command came to
                    ShowOutcomePanel(invocation.Command.Name, ex.Message, TerminalTheme.ErrorColor);
                    Repaint(ctx);
                }
            }
        }
        catch (Exception ex)
        {
            // Logging is silenced under the live UI, so the outcome panel is the only place the failure can surface
            lock (renderLock)
            {
                // After the user left there is no screen to draw on
                if (!exitRequested)
                {
                    ShowOutcomePanel(invocation.Command.Name, $"failed: {ex.Message}", TerminalTheme.ErrorColor);
                    Repaint(ctx);
                }
            }
        }
        finally
        {
            // The command is over: whatever it was drawing goes with it, one that died halfway through included.
            // Only /new leaves a turn open behind it, waiting on the fresh conversation's presentation
            commandRunning = false;
            lock (renderLock)
            {
                // A turn left open by /new keeps its own bar
                if (!awaitingResponse)
                    commandProgress.Clear();

                // The prompt comes back whatever the command left: the hint holding it goes. An outcome that
                // landed while the command still ran is drawn only now. After the user left there is no screen
                if (!exitRequested)
                    Repaint(ctx);
            }
        }
    }

    /// <summary>
    /// The conversation as the palette must judge it: whether the budget is spent and whether an agent is
    /// carrying it. Read under <see cref="renderLock"/> by every caller.
    /// </summary>
    private TerminalConversationState ConversationState() =>
        new(conversationDead, agentCarryingConversation is not null);

    /// <inheritdoc />
    public TerminalSessionStatus Status
    {
        get
        {
            // The status is read in one moment so /status never disagrees with the header
            lock (renderLock)
                return new TerminalSessionStatus(currentSpeaker, reportedDustLevel, conversationDead, history.Count(entry => entry.IsConversationMessage));
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<CommandDescriptor> AvailableCommands
    {
        get
        {
            // The palette is judged in one moment with the header
            lock (renderLock)
                return commandRegistry.ListAvailableCommands(ConversationState());
        }
    }

    /// <summary>Opens a turn on the wire, from which the silence deadline is measured. Must be called under <see cref="renderLock"/>.</summary>
    private void BeginTurn()
    {
        awaitingResponse = true;

        // The conversation has moved on, so what the last command came to is no longer news
        promptNotice = null;
    }

    /// <summary>
    /// Gives the prompt back at the end of a turn, however it ended. A bar a command left standing belongs to
    /// work that is over, so it goes with the turn rather than sitting where the caret is drawn.
    /// Must be called under <see cref="renderLock"/>.
    /// </summary>
    private void ReleaseTurn()
    {
        awaitingResponse = false;
        commandProgress.Clear();
    }

    /// <summary>Writes a notice into the transcript in <paramref name="color"/> and brings the view back to it.</summary>
    private void ShowSystemNotice(LiveDisplayContext ctx, string text, string color)
    {
        lock (renderLock)
        {
            // A command still finishing after the user left must not draw over the shell the display gave back
            if (exitRequested)
                return;

            // The notice joins the transcript under the channel's own name
            history.Add(new DisplayedMessage(profile.ChannelName, text, color, ChannelMessageTypes.SystemWarning));

            // A notice scrolled out of sight would leave the user wondering why nothing happened
            scrollOffset = 0;
            Repaint(ctx);
        }
    }

    /// <inheritdoc />
    public void RequestExit()
    {
        // The key loop stops on the flag and completing the queue releases the drain loop, so the live display
        // returns. A command still waiting on Morgana is called off, so leaving never waits for it
        exitRequested = true;
        commandCancellation.Cancel();
        inbound.Writer.TryComplete();
    }

    /// <inheritdoc />
    public void ShowProgress(CommandProgress frame)
    {
        lock (renderLock)
        {
            // The frame replaces the one before it
            commandProgress.Show(frame);
            Repaint(LiveDisplay);
        }
    }

    /// <inheritdoc />
    public void ShowCommandOutcome(string text, bool isFailure = false)
    {
        lock (renderLock)
        {
            // A command run here reports its own outcome: nothing is left owed to it from Morgana
            invocationOwingOutcome = null;

            // A command still finishing after the user left must not draw over the shell the display gave back
            if (exitRequested)
                return;

            // The panel shows what the command came to under its name
            ShowOutcomePanel(runningCommandName, text, isFailure ? TerminalTheme.ErrorColor : TerminalTheme.CommandReportColor);
            Repaint(LiveDisplay);
        }
    }

    /// <inheritdoc />
    public void ShowPanel(Func<int, IReadOnlyList<Markup>> layOut)
    {
        lock (renderLock)
        {
            // A command run here reports its own outcome: nothing is left owed to it from Morgana
            invocationOwingOutcome = null;

            // A command still finishing after the user left must not draw over the shell the display gave back
            if (exitRequested)
                return;

            // The panel replaces the body until it is closed
            commandPanel = layOut;
            Repaint(LiveDisplay);
        }
    }

    /// <summary>Closes the open panel on Esc or Enter and swallows every other key; false when no panel is open.</summary>
    private bool TryHandlePanelKey(LiveDisplayContext ctx, ConsoleKey key)
    {
        lock (renderLock)
        {
            // With no panel open the key is not for it
            if (commandPanel is null)
                return false;

            // Esc here closes the panel rather than leaving the channel: the user is dismissing what they asked to see
            if (key is ConsoleKey.Escape or ConsoleKey.Enter)
            {
                commandPanel = null;
                Repaint(ctx);
            }

            // Whatever the key was, the panel took it
            return true;
        }
    }

    /// <summary>Takes a frame of a command run on Morgana: its bar while the command runs, then the finished frame's text as the command's outcome.</summary>
    private void HandleInboundProgress(LiveDisplayContext ctx, ChannelMessage message)
    {
        // The drain loop routes here only the messages that carry a frame
        CommandProgress frame = message.Progress!;
        lock (renderLock)
        {
            // Only the command still owed an outcome is heard. Its bar only while it runs: a frame landing after
            // the command returned would stand over the prompt with nothing left to take it down. Its outcome
            // whenever it lands, since the drain may deliver it after the call returned or after its deadline
            if (!BelongsToRunOwingOutcome(frame) || (!frame.Finished && !commandRunning))
                return;

            // The frame replaces the one before it
            commandProgress.Show(frame);
            if (frame.Finished)
            {
                // The finished frame's text is the command's outcome
                ShowOutcomePanel(frame.Command, message.Text, OutcomeColor(message));
                invocationOwingOutcome = null;

                // A budget found spent ends the conversation whatever asked for more of it, a command included
                if (string.Equals(message.ErrorReason, ChannelErrorReasons.DustBudgetExhausted, StringComparison.Ordinal))
                    MarkConversationSpent();
            }

            // The bar or the outcome is shown
            Repaint(ctx);
        }
    }

    /// <summary>Latches the conversation as spent: the prompt takes only the ways out of it. Must be called under <see cref="renderLock"/>.</summary>
    private void MarkConversationSpent()
    {
        conversationDead = true;

        // A half-typed line is doomed since Morgana would refuse it; options already on offer lead nowhere
        currentInput = string.Empty;
        cursorPosition = 0;
        quickReplyPrompt.Dismiss();
    }

    /// <summary>The colour of an outcome Morgana delivered: a rate-limit refusal reads as a warning, a spent budget as an error and anything else as the command's own report.</summary>
    private static string OutcomeColor(ChannelMessage message) => message.ErrorReason switch
    {
        ChannelErrorReasons.RateLimitExceeded => TerminalTheme.WarningColor,
        ChannelErrorReasons.DustBudgetExhausted => TerminalTheme.ErrorColor,
        _ => TerminalTheme.CommandReportColor
    };

    /// <summary>Draws <paramref name="text"/> above the prompt in <paramref name="color"/> as the reason the command line ran nothing.</summary>
    private void ShowPromptNotice(LiveDisplayContext ctx, string text, string color)
    {
        // The notice and its colour are shared with the render path
        lock (renderLock)
        {
            // A command still finishing after the user left must not draw over the shell the display gave back
            if (exitRequested)
                return;

            // The notice quotes the line the user typed, pasted text included, so it is drawn only once it is plain text
            promptNotice = TerminalCellService.StripControlCharacters(text);
            promptNoticeColor = color;
            Repaint(ctx);
        }
    }

    /// <summary>
    /// Opens the panel reporting what one of Morgana's commands came to: its name as the title, the outcome under it in
    /// <paramref name="color"/>. An outcome landing after the deadline's replaces the deadline's. Must be called under <see cref="renderLock"/>.
    /// </summary>
    private void ShowOutcomePanel(string commandName, string text, string color)
    {
        // Morgana's outcome arrives over the unauthenticated callback, so it is drawn only once it is plain text
        string outcome = TerminalCellService.StripControlCharacters(text);
        string title = "/" + TerminalCellService.StripControlCharacters(commandName);
        commandPanel = width =>
            [cells.RenderPanelTitle(title, width, theme.MorganaColor), .. cells.WrapWords(outcome, width).Select(row => new Markup($"[{color}]{Markup.Escape(row)}[/]"))];
    }

    /// <summary>Echoes <paramref name="echo"/> as the user's line, then sends the turn through <paramref name="dispatch"/> under the reply deadline.</summary>
    private async Task SubmitTurnAsync(LiveDisplayContext ctx, string echo, Func<Task> dispatch)
    {
        lock (renderLock)
        {
            // A turn sent while quick replies are pending answers them: they do not come back after the reply
            quickReplyPrompt.Dismiss();

            // Echo and thinking hint go up before the send, so a slow backend still feels answered
            history.Add(new DisplayedMessage(TerminalTheme.UserSpeaker, echo, TerminalTheme.UserColor, "user"));
            BeginTurn();

            // The view jumps back to the live bottom for the new turn
            scrollOffset = 0;
            Repaint(ctx);
        }

        try
        {
            // The turn is on its way to Morgana
            await dispatch();

            // Morgana took the turn: from here only its delivery can reopen the prompt, so that delivery gets a
            // deadline. A turn already answered has nothing to watch
            lock (renderLock)
            {
                if (awaitingResponse)
                    StartReplyWatchdog(ctx);
            }
        }
        catch (Exception ex)
        {
            // The failure is shown in the UI since logging is silenced; the gate is released so the user can
            // retry without waiting for a delivery that will never arrive. A send called off because the user left draws nothing
            lock (renderLock)
            {
                if (exitRequested)
                    return;
                history.Add(new DisplayedMessage(profile.ChannelName, $"send failed: {ex.Message}", TerminalTheme.ErrorColor, ChannelMessageTypes.Error));
                ReleaseTurn();
                Repaint(ctx);
            }
        }
    }

    /// <inheritdoc />
    public async Task ReplaceConversationAsync(Func<CancellationToken, Task<string>> openConversation, CancellationToken cancellationToken)
    {
        LiveDisplayContext ctx = LiveDisplay;

        // The new conversation's presentation may land before its id is known here: it waits in the queue
        // until the screen has been cleared for it, instead of joining the transcript about to be wiped
        TaskCompletionSource deliveriesRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref deliveriesReleased, deliveriesRelease.Task);
        try
        {
            // The prompt waits while the conversation is opened, as it waits for a reply
            lock (renderLock)
            {
                BeginTurn();
                scrollOffset = 0;
                Repaint(ctx);
            }

            // The id Morgana gave the new conversation
            string openedConversationId;
            try
            {
                // The command opens the conversation and hands back its id
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
                    Repaint(ctx);
                }

                // The command reports why, through the failure notice of RunCommandAsync
                throw;
            }

            // The new conversation replaces everything on screen
            lock (renderLock)
            {
                // Opened just as the user left: the lifecycle ends whichever conversation the session names by then
                if (exitRequested)
                    return;
                ResetScreenForConversation(openedConversationId);

                // The fresh conversation's presentation is a reply like any other and gets the same deadline
                BeginTurn();
                StartReplyWatchdog(ctx);
                Repaint(ctx);
            }
        }
        finally
        {
            // Deliveries resume however the swap ended: the drain loop drops those of a conversation not on screen
            deliveriesRelease.TrySetResult();
        }
    }

    /// <summary>Clears everything the previous conversation left on screen and names <paramref name="openedConversationId"/> in the header. Must be called under <see cref="renderLock"/>.</summary>
    private void ResetScreenForConversation(string openedConversationId)
    {
        // Nothing of the old turn may fire later: no deadline notice, no tick on a cleared pane. A reply still
        // being revealed belongs to the conversation left behind
        CancelReplyWatchdog();
        streamingPane.Reset();

        // The transcript and its wrapped rows go together: the next frame must not draw rows of a gone history
        history.Clear();
        historyRows.Clear();
        historyRowsMessageCount = 0;

        // Header back to base Morgana; the gauge stays hidden until the new conversation reports its dust
        currentSpeaker = TerminalTheme.MorganaSpeaker;
        agentCarryingConversation = null;
        conversationId = openedConversationId;
        dustGaugeMarkup = string.Empty;
        reportedDustLevel = null;

        // A fresh budget: the spent latch and any pending choice of the old conversation are gone
        conversationDead = false;
        quickReplyPrompt.Dismiss();

        // The view and the prompt start clean, the /new line and any command outcome included
        promptNotice = null;
        scrollOffset = 0;
        currentInput = string.Empty;
        cursorPosition = 0;
    }

    /// <summary>
    /// Gives the turn in flight <see cref="replyTimeout"/> of silence before the user gets the prompt back with an honest
    /// notice. Every sign of life arms it again from zero, so only a turn Morgana has abandoned expires.
    /// Must be called under <see cref="renderLock"/>.
    /// </summary>
    private void StartReplyWatchdog(LiveDisplayContext ctx)
    {
        CancelReplyWatchdog();
        CancellationTokenSource watchdog = CancellationTokenSource.CreateLinkedTokenSource(uiStopping);
        replyWatchdog = watchdog;

        // The deadline runs apart from the caller and ends with the turn
        _ = Task.Run(async () =>
        {
            try
            {
                // The silence is measured from now
                await Task.Delay(replyTimeout, watchdog.Token);
            }
            catch (OperationCanceledException)
            {
                // Morgana spoke in time or the process is going down: whoever called the deadline off owns it
                return;
            }

            // The expiry is judged under the lock against what happened meanwhile
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

                // Text still being revealed means the turn is alive and owed the rest of its words, however long the reveal takes
                if (streamingPane.IsRevealing)
                {
                    StartReplyWatchdog(ctx);
                    return;
                }

                // What the stream did reveal is what Morgana actually said, so it stays as the agent's message it was
                // rather than hanging in a pane the next turn would write underneath
                string revealedText = streamingPane.FlushPartial();
                if (revealedText.Length > 0)
                    history.Add(new DisplayedMessage(currentSpeaker, revealedText, theme.SpeakerColor(currentSpeaker), ChannelMessageTypes.Assistant));

                // The notice tells the user the turn may be lost
                history.Add(new DisplayedMessage(
                    profile.ChannelName,
                    $"no answer from Morgana after {replyTimeout.TotalSeconds:0}s — the turn may be lost, ask again or press Esc to quit",
                    TerminalTheme.ErrorColor,
                    ChannelMessageTypes.Error));
                ReleaseTurn();
                Repaint(ctx);
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

    /// <summary>Maps a key to a scroll direction: Up and PageUp move towards older content, Down and PageDown back to the present. Left and Right stay with the caret.</summary>
    private static bool TryScrollDelta(ConsoleKey key, out int delta)
    {
        switch (key)
        {
            // Towards older content
            case ConsoleKey.UpArrow or ConsoleKey.PageUp:
                delta = ScrollStep;
                return true;

            // Back towards the present
            case ConsoleKey.DownArrow or ConsoleKey.PageDown:
                delta = -ScrollStep;
                return true;

            // Any other key is no scrolling
            default:
                delta = 0;
                return false;
        }
    }

    /// <summary>Advances the scrollback by <paramref name="delta"/> rows and repaints; the upper bound is clamped against the live content in <see cref="BuildBody"/>.</summary>
    private void ApplyScroll(LiveDisplayContext ctx, int delta)
    {
        lock (renderLock)
        {
            // The floor is the live bottom
            scrollOffset = Math.Max(0, scrollOffset + delta);
            Repaint(ctx);
        }
    }

    /// <summary>
    /// Repaints the screen unless further keystrokes are already queued, so a pasted line costs one frame
    /// instead of one per character. Must be called under <see cref="renderLock"/>.
    /// </summary>
    private void RefreshWhenInputSettles(LiveDisplayContext ctx)
    {
        try
        {
            // Keys already waiting mean the frame would be stale at once
            if (Console.KeyAvailable)
                return;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            // The terminal is gone: the frame is painted anyway and the key loop takes the exit path
        }

        // The input has settled: this is the frame worth painting
        Repaint(ctx);
    }

    /// <summary>Builds the two-row Spectre layout: a fixed 3-row header over a flexible body.</summary>
    private Layout BuildLayout()
    {
        Layout root = new Layout("root")
            .SplitRows(
                new Layout("header").Size(3),
                new Layout("body").Ratio(1));

        // The body comes first: it recomputes the scroll flags that the header's arrows read, so they
        // reflect this same frame rather than lagging one behind
        IRenderable body = BuildBody();
        root["header"].Update(BuildHeader());
        root["body"].Update(body);
        return root;
    }

    /// <summary>Builds the sticky header: the speaker, the truncated conversation id, the dust gauge, the scroll arrows and the input counter.</summary>
    private IRenderable BuildHeader()
    {
        // The speaker colour follows the speaker flipped to the agent mid-turn and back to Morgana on
        // completion, so the header always mirrors who holds the floor now
        string speakerColor = theme.SpeakerColor(currentSpeaker);

        // Conversation ids are 32 hex characters: truncated so the header stays readable on a narrow terminal
        string shortId = conversationId.Length > 12 ? conversationId[..12] + "…" : conversationId;

        // Each arrow is lit in the user's colour when scrolling that way is possible and dim otherwise: scrolling
        // is the user's own action, not the speaker's. The segment is left out unless one arrow is actionable,
        // so a conversation that fits keeps a clean header. BuildBody set the flags for this same frame
        string scrollSegment = scrollHasAbove || scrollHasBelow
            ? $"   {(scrollHasAbove ? $"[{TerminalTheme.UserColor}]▲[/]" : "[grey50]▲[/]")}{(scrollHasBelow ? $"[{TerminalTheme.UserColor}]▼[/]" : "[grey50]▼[/]")}"
            : string.Empty;

        // The input counter shows only while a text prompt is being typed into, once a character is in it. It is
        // the prompt's own dust gauge: white, orange from 70% and red at the cap, so swallowed keystrokes at the cap read as a limit
        string countSegment = string.Empty;
        if (!awaitingResponse && !conversationDead && !quickReplyPrompt.IsOffering && currentInput.Length >= 1)
        {
            // The colour of the counter follows how close the line is to the cap
            string countColor =
                currentInput.Length >= maxInputLength ? TerminalTheme.DustCriticalColor :
                currentInput.Length * 10 >= maxInputLength * 7 ? TerminalTheme.DustLowColor :
                TerminalTheme.UserColor;
            countSegment = $"   [grey54]chars[/] [bold {countColor}]{currentInput.Length}/{maxInputLength}[/]";
        }

        // The speaker name arrives over the network, so it is escaped before it can break the markup
        Markup content = new(
            $"[bold {speakerColor}]{Markup.Escape(currentSpeaker)}[/]   " +
            $"[grey54]conv[/] [bold {theme.MorganaColor}]{Markup.Escape(shortId)}[/]" +
            dustGaugeMarkup + scrollSegment + countSegment);

        // The header is a rounded panel centred on its content
        return new Panel(Align.Center(content, VerticalAlignment.Middle))
        {
            Border = BoxBorder.Rounded,

            // The frame wears the speaker's colour, so who holds the floor reads from the whole band
            BorderStyle = Style.Parse(speakerColor),
            Header = new PanelHeader(""),
            Padding = new Padding(1, 0, 1, 0),
            Expand = true
        };
    }

    /// <summary>
    /// Builds the body as a flat stream of single-row renderables: the tail of the transcript and of the streaming
    /// pane on top, the prompt and the palette pinned at the bottom.
    /// </summary>
    /// <remarks>Every row is exactly one terminal row, so the window can be cut by counting; an input taller than the body loses its oldest typed rows, since the caret row is the floor of the prompt.</remarks>
    private IRenderable BuildBody()
    {
        TerminalViewport viewport = ReadViewport();
        int bodyHeight = Math.Max(0, viewport.Height - 3 /* header panel */);

        // An open panel is all the body shows, so it never mixes with the transcript or the prompt it covers
        if (commandPanel is { } layOutPanel)
            return BuildPanelBody(layOutPanel, viewport.Width, bodyHeight);

        // The prompt is pinned at the bottom and takes what it needs first
        List<IRenderable> inputRows = BuildInputRows(viewport.Width);
        if (inputRows.Count > bodyHeight && bodyHeight > 0)
        {
            // The input alone overflows the body: its oldest typed rows are dropped so the caret row stays visible
            inputRows = inputRows.GetRange(inputRows.Count - bodyHeight, bodyHeight);
        }

        // The scrollable content is the whole conversation: the transcript rows followed by the reply being revealed
        RenderNewHistoryRows(viewport.Width);
        List<Markup> streamingRows = streamingPane.RenderRows(viewport.Width);
        int contentRowCount = historyRows.Count + streamingRows.Count;

        // The palette hangs under the prompt and takes only the rows the prompt leaves, so a short terminal
        // loses palette rows before it loses the caret. A form asking for a value keeps the rows under the
        // prompt: a path starting with a slash would otherwise list every command under the question it answers
        List<Markup> paletteRows = awaitingResponse || commandOptionPrompt.IsActive
            ? []
            : commandPalette.RenderPalette(currentInput, ConversationState(), viewport.Width);
        int paletteBudget = Math.Max(0, bodyHeight - inputRows.Count);
        if (paletteRows.Count > paletteBudget)
            paletteRows = paletteRows.GetRange(0, paletteBudget);

        // What the prompt and the palette leave is the transcript's
        int contentHeight = Math.Max(0, bodyHeight - inputRows.Count - paletteRows.Count);

        // The window is anchored by counting rows up from the bottom, clamped to the live content so a resize
        // or a shorter conversation cannot strand it off the end. Scrolling is enabled only at rest, so
        // during a turn the offset is 0 and the window sits on the bottom
        int maxOffset = Math.Max(0, contentRowCount - contentHeight);
        scrollOffset = Math.Clamp(scrollOffset, 0, maxOffset);
        int windowEnd = contentRowCount - scrollOffset;
        int windowStart = Math.Max(0, windowEnd - contentHeight);

        // The arrows light only when scrolling is actionable: not mid-turn and not while quick replies hold the keys
        bool scrollable = !quickReplyPrompt.IsOffering && !awaitingResponse;
        scrollHasAbove = scrollable && windowStart > 0;
        scrollHasBelow = scrollable && scrollOffset > 0;

        // The window is cut from the transcript followed by the streamed rows
        List<IRenderable> rows = new(contentHeight + inputRows.Count + paletteRows.Count);
        for (int index = windowStart; index < windowEnd; index++)
            rows.Add(index < historyRows.Count ? historyRows[index] : streamingRows[index - historyRows.Count]);
        rows.AddRange(inputRows);
        rows.AddRange(paletteRows);
        return new Rows(rows);
    }

    /// <summary>The open panel as the body: its rows from the top, then the line telling how it closes, cut to the body's height.</summary>
    private Rows BuildPanelBody(Func<int, IReadOnlyList<Markup>> layOutPanel, int termWidth, int bodyHeight)
    {
        // Nothing underneath can be scrolled while the panel covers it, so the header offers no arrows
        scrollHasAbove = false;
        scrollHasBelow = false;

        // The closing line is kept whatever the height, since a panel nobody can see how to leave is a trap
        Markup closingHint = new($"[grey54 italic]{Markup.Escape(cells.Trunc("Esc or Enter to close", termWidth))}[/]");
        List<IRenderable> rows = [.. layOutPanel(termWidth).Take(Math.Max(0, bodyHeight - 1)), closingHint];
        return new Rows(rows);
    }

    /// <summary>
    /// Current terminal size, falling back to the last one seen when the terminal no longer answers: a window
    /// closed mid-frame must not break the repaint in progress, since the process leaves through SIGHUP moments later.
    /// </summary>
    private TerminalViewport ReadViewport()
    {
        try
        {
            // The size is remembered for when the terminal stops answering
            lastViewportWidth = Math.Max(1, Console.WindowWidth);
            lastViewportHeight = Math.Max(0, Console.WindowHeight);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            // The terminal is disconnected: the last known size is the best answer available
        }

        // The size the frame is drawn against
        return new TerminalViewport(lastViewportWidth, lastViewportHeight);
    }

    /// <summary>Brings <see cref="historyRows"/> up to date with <see cref="history"/> at <paramref name="termWidth"/>, rendering only the messages added since the previous frame unless the width changed.</summary>
    private void RenderNewHistoryRows(int termWidth)
    {
        // Rows wrapped at another width are wrong, so a resize renders the whole history again
        if (historyRowsWidth != termWidth)
        {
            historyRows.Clear();
            historyRowsMessageCount = 0;
            historyRowsWidth = termWidth;
        }

        // The history only grows, so the rows of the messages already rendered are kept
        for (; historyRowsMessageCount < history.Count; historyRowsMessageCount++)
            historyRows.AddRange(messageRenderer.RenderMessage(history[historyRowsMessageCount], termWidth));
    }

    /// <summary>
    /// Builds the prompt area as single-row renderables. The surfaces are tried in precedence: a confirmation
    /// question, a command's progress, the running hint, a spent conversation, the quick replies, the thinking hint
    /// and finally the free-text prompt.
    /// </summary>
    /// <remarks>Rows are packed to the width here, so Spectre never wraps one behind the budget the transcript is cut against.</remarks>
    private List<IRenderable> BuildInputRows(int termWidth)
    {
        // A command waiting on an answer takes the prompt over entirely: until it is answered there is nothing
        // else the user can do here, a spent conversation and pending quick replies included
        if (commandConfirmation.IsPending)
            return FramePromptRows([.. commandConfirmation.RenderQuestion(termWidth)], termWidth);

        // The form asks above the caret: the question and the value being typed have to be read together
        List<IRenderable> optionPromptRows = [.. commandOptionPrompt.RenderPrompt(termWidth)];

        // A command reporting its progress says more than the thinking hint it stands in for: it names the step
        // being worked on. It holds the prompt for as long as it runs, here or on Morgana, since a command is answered, never talked over
        if ((awaitingResponse || commandRunning) && commandProgress.IsActive)
            return FramePromptRows([.. commandProgress.RenderProgress(termWidth)], termWidth);

        // A command that has not reported yet still holds the prompt and says so: a line typed now would be lost
        if (commandRunning && !awaitingResponse)
            return ChunkStyledRows($"/{runningCommandName} is running…", termWidth, $"{TerminalTheme.CommandReportColor} italic");

        // Why the last command line ran nothing sits above whatever the prompt is now, where it was typed
        List<IRenderable> outcomeRows = promptNotice is { } notice
            ? ChunkStyledRows(notice, termWidth, promptNoticeColor)
            : [];

        // A spent conversation supersedes everything but a command line being typed, which is how the user gets
        // out of it. Morgana's banner already told the user the dust ran out; here they learn the way on
        if (conversationDead && !IsCommandLineOpen())
            return [.. outcomeRows, .. ChunkStyledRows("✦ Conversation spent — type /new to start a fresh one or press Esc to quit", termWidth, $"{TerminalTheme.ErrorColor} italic")];

        // Quick replies own the prompt for this turn, framed like the free-text prompt since picking one is still the user's turn to act
        if (quickReplyPrompt.IsOffering && !IsCommandLineOpen())
            return FramePromptRows([.. outcomeRows, .. quickReplyPrompt.RenderPrompt(termWidth)], termWidth);

        // A turn in flight shows a hint instead of a prompt
        if (awaitingResponse)
        {
            // While chunks are landing the streaming pane is the focal element; the thinking hint
            // would compete with it for the bottom row
            if (streamingPane.HasRevealedText)
                return [];

            // The hint wears the colour of whoever is to answer, so it signals the speaker before the first word arrives
            return ChunkStyledRows($"{currentSpeaker} is thinking…", termWidth, $"{theme.SpeakerColor(currentSpeaker)} italic");
        }

        // The free-text prompt is the last surface
        return FramePromptRows([.. outcomeRows, .. optionPromptRows, .. BuildPromptLineRows(termWidth)], termWidth);
    }

    /// <summary>
    /// Draws the free-text line: the chevron, the typed runes and the caret, packed into rows of at most
    /// <paramref name="termWidth"/> cells. A wide rune counts as two cells so the caret stays put and no row wraps
    /// silently; a surrogate pair is never split across rows.
    /// </summary>
    private List<IRenderable> BuildPromptLineRows(int termWidth)
    {
        // Each unit is one glyph with the cells it occupies; the chevron and its space come first
        List<(string Markup, int Cells)> units =
        [
            ($"[{TerminalTheme.UserColor}]›[/]", 1),
            (" ", 1)
        ];
        int charOffset = 0;
        bool caretPlaced = false;

        // A line naming a command exactly takes the match colour, as in Claude Code: Enter will run what is written
        bool namesCommand = !commandOptionPrompt.IsActive && commandPalette.NamesCommandExactly(currentInput, ConversationState());
        foreach (Rune rune in currentInput.EnumerateRunes())
        {
            // The rune is escaped before it is drawn
            string glyph = Markup.Escape(rune.ToString());
            if (namesCommand)
                glyph = $"[{commandPalette.MatchColor}]{glyph}[/]";

            // The caret is drawn on the rune it sits before, as an inverted block, so it stays visible mid-line.
            // It is kept as a char index and mapped to its rune by tracking the char offset
            if (!caretPlaced && cursorPosition >= charOffset && cursorPosition < charOffset + rune.Utf16SequenceLength)
            {
                units.Add(($"[blink {TerminalTheme.UserColor} invert]{glyph}[/]", cells.RuneCells(rune)));
                caretPlaced = true;
            }
            else
                units.Add((glyph, cells.RuneCells(rune)));
            charOffset += rune.Utf16SequenceLength;
        }

        // A caret at the end of the line has no rune to sit on and blinks as an underscore after the last one
        if (!caretPlaced)
            units.Add(($"[blink {TerminalTheme.UserColor}]_[/]", 1));

        // The rows the line is packed into
        List<IRenderable> rows = [];
        StringBuilder rowMarkup = new(capacity: termWidth + 32 /* slack for style tags */);
        int rowCells = 0;
        foreach ((string markup, int unitCells) in units)
        {
            // A row closes before a unit that would overflow it, but never while empty, so a unit wider than the whole width still lands somewhere
            if (rowCells + unitCells > termWidth && rowMarkup.Length > 0)
            {
                rows.Add(new Markup(rowMarkup.ToString()));
                rowMarkup.Clear();
                rowCells = 0;
            }
            // The unit joins the row being built
            rowMarkup.Append(markup);
            rowCells += unitCells;
        }

        // The last row holds whatever remains
        if (rowMarkup.Length > 0)
            rows.Add(new Markup(rowMarkup.ToString()));
        return rows;
    }

    /// <summary>Sandwiches the rows where the user acts between two full-width rules, so that surface is not marked only by the caret or the highlight. The thinking hint and the spent notice are status and go unframed.</summary>
    private static List<IRenderable> FramePromptRows(List<IRenderable> rows, int termWidth)
    {
        // Nothing to frame: bordering emptiness would steal two rows from the transcript for no gain
        if (rows.Count == 0)
            return rows;

        // Neutral grey keeps the frame as plain chrome: the speaker colour is spoken for by the header band
        Markup border = new($"[grey50]{new string('─', Math.Max(1, termWidth))}[/]");
        return [border, .. rows, border];
    }

    /// <summary>Splits <paramref name="content"/> into rows of at most <paramref name="termWidth"/> cells in <paramref name="style"/>; an empty text gives one empty row.</summary>
    private List<IRenderable> ChunkStyledRows(string content, int termWidth, string style)
    {
        // An empty text still occupies its row
        if (content.Length == 0)
            return [new Markup(string.Empty)];

        // Measured in cells rather than characters: a wide glyph in a command's outcome would otherwise overflow its row behind the budget
        List<IRenderable> rows = [];
        foreach (string slice in cells.Wrap(content, termWidth))
            rows.Add(new Markup($"[{style}]{Markup.Escape(slice)}[/]"));
        return rows;
    }

    /// <summary>UTF-16 length (1 or 2 for a surrogate pair) of the rune ending just before <paramref name="position"/> in <see cref="currentInput"/>; keeps the caret on a rune boundary going left. The caller guarantees <paramref name="position"/> is above 0.</summary>
    private int RuneLengthBefore(int position) =>
        position >= 2 && char.IsLowSurrogate(currentInput[position - 1]) && char.IsHighSurrogate(currentInput[position - 2]) ? 2 : 1;

    /// <summary>UTF-16 length (1 or 2 for a surrogate pair) of the rune starting at <paramref name="position"/> in <see cref="currentInput"/>; keeps the caret on a rune boundary going right. The caller guarantees <paramref name="position"/> is below the length.</summary>
    private int RuneLengthAt(int position) =>
        position + 1 < currentInput.Length && char.IsHighSurrogate(currentInput[position]) && char.IsLowSurrogate(currentInput[position + 1]) ? 2 : 1;

    /// <summary>
    /// The colour of an inbound message's rows: error notices red, advisory warnings orange and anything else
    /// the speaker's own. What a command reports never reaches the transcript.
    /// </summary>
    private string RowColor(ChannelMessage message, string speaker)
    {
        // An error notice such as an exhausted budget or a failed delivery
        if (string.Equals(message.MessageType, ChannelMessageTypes.Error, StringComparison.OrdinalIgnoreCase))
            return TerminalTheme.ErrorColor;

        // An advisory warning such as a rate limit or a low budget
        if (string.Equals(message.MessageType, ChannelMessageTypes.SystemWarning, StringComparison.OrdinalIgnoreCase))
            return TerminalTheme.WarningColor;

        // An ordinary turn
        return theme.SpeakerColor(speaker);
    }

    /// <summary>Mirrors Cauldron's rule: a specialised agent announces itself as <c>Morgana (Intent)</c>, so parentheses tell a domain agent from base Morgana.</summary>
    private static bool IsSpecializedAgent(string? agentName) =>
        agentName is not null && agentName.Contains('(') && agentName.Contains(')');

    /// <summary>One item of the inbound queue: a delivery or a streamed chunk, both addressed to a conversation.</summary>
    private abstract record InboundEvent(string ConversationId);

    /// <summary>A full message that arrived on the message endpoint.</summary>
    private sealed record MessageEvent(ChannelMessage Message) : InboundEvent(Message.ConversationId);

    /// <summary>An incremental delta that arrived on the chunk endpoint.</summary>
    private sealed record ChunkEvent(string ConversationId, string ChunkText) : InboundEvent(ConversationId);
}
