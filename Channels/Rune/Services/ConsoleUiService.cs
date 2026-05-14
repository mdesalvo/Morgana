using System.Text;
using System.Threading.Channels;
using Rune.Interfaces;
using Rune.Messages.Contracts;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Rune.Services;

/// <summary>
/// Terminal UI for Rune built on Spectre.Console's <see cref="LiveDisplay"/>: a sticky
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
    /// <summary>Color for base Morgana turns.</summary>
    private const string MorganaColor = "magenta1";

    /// <summary>Color for specialised agent turns (<c>Morgana (Agent)</c>).</summary>
    private const string MorganaAgentColor = "hotpink";

    /// <summary>Color for the user's own input and committed lines.</summary>
    private const string UserColor = "white";

    /// <summary>Fallback for <c>Rune:AgentExitMessage</c> when the setting is absent.</summary>
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

    /// <summary>Thread-safe queue of messages posted by <see cref="WebhookReceiverService"/> awaiting render.</summary>
    private readonly Channel<ChannelMessage> incoming = Channel.CreateUnbounded<ChannelMessage>();

    /// <summary>Format string for the courtesy line appended when a specialised agent completes.</summary>
    private readonly string agentExitTemplate;

    /// <summary>Buffer holding keystrokes not yet committed with <see cref="ConsoleKey.Enter"/>.</summary>
    private string currentInput = string.Empty;

    /// <summary>Name shown in the sticky header; swaps to the agent's name mid-turn and back to <c>Morgana</c> on completion.</summary>
    private string currentSpeaker = "Morgana";

    /// <summary>Current conversation id, displayed (truncated) in the header.</summary>
    private string conversationId = string.Empty;

    /// <summary>Set once the user types <c>/quit</c> or presses <see cref="ConsoleKey.Escape"/>.</summary>
    private volatile bool exitRequested;

    /// <summary>When true, keystrokes (except Esc) are swallowed and the input line shows a thinking hint. Starts true: Rune always waits for Morgana's presentation before the first user turn.</summary>
    private volatile bool awaitingResponse = true;

    /// <summary>Platform-specific terminal-resize notifier; subscribed in <see cref="RunAsync"/> so the viewport anchor follows live window resizes without per-frame polling.</summary>
    private readonly IViewportResizeWatcher viewportResizeWatcher;

    /// <summary>Serializes mutations of <see cref="history"/>, <see cref="currentInput"/>, <see cref="currentSpeaker"/> and the paired <see cref="LiveDisplayContext.UpdateTarget"/>/<see cref="LiveDisplayContext.Refresh"/> calls. The resize callback runs on its own thread (SIGWINCH handler / polling task) and would otherwise race with <see cref="ReadKeysLoop"/> and <see cref="DrainIncomingLoop"/> over the shared list. Uses <see cref="System.Threading.Lock"/> (.NET 9+) instead of <c>object</c> so the compiler emits the optimised primitive and rejects misuse (e.g. passing the lock as an <c>object</c>).</summary>
    private readonly Lock renderLock = new();

    /// <summary>Reads the <c>Rune:AgentExitMessage</c> template, falling back to <see cref="DefaultAgentExitMessage"/>, and captures the injected resize watcher.</summary>
    public ConsoleUiService(IConfiguration configuration, IViewportResizeWatcher viewportResizeWatcher)
    {
        agentExitTemplate = configuration["Rune:AgentExitMessage"] ?? DefaultAgentExitMessage;
        this.viewportResizeWatcher = viewportResizeWatcher;
    }

    /// <summary>Called by <see cref="WebhookReceiverService"/> when Morgana delivers a message.</summary>
    public void EnqueueIncoming(ChannelMessage message) => incoming.Writer.TryWrite(message);

    /// <summary>
    /// Starts the live terminal UI. Returns when the user types <c>/quit</c> or the cancellation
    /// token fires. <paramref name="onSend"/> is invoked on each committed user input line.
    /// </summary>
    public async Task RunAsync(
        string conversationId,
        Func<string, Task> onSend,
        CancellationToken cancellationToken = default)
    {
        this.conversationId = conversationId;

        Layout layout = BuildLayout();

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
                    history.Add(new DisplayedMessage(messageSpeaker, message.Text, SpeakerColor(messageSpeaker)));

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

                    // Release the input gate: ReadKeysLoop was swallowing keystrokes until
                    // this first webhook delivery landed.
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

            // Swallow every keystroke that isn't an explicit exit while we're waiting for
            // Morgana to speak. Esc is always honoured so the user can bail out even
            // mid-turn; everything else (printable characters, Enter, Backspace) is a no-op.
            if (awaitingResponse && key.Key != ConsoleKey.Escape)
                continue;

            switch (key.Key)
            {
                case ConsoleKey.Enter when currentInput.Length > 0:
                {
                    // Snapshot-and-clear before awaiting: any late keystroke during onSend
                    // must land on a fresh buffer, not reappend to the line we just sent.
                    string toSend;
                    lock (renderLock)
                    {
                        toSend = currentInput;
                        currentInput = string.Empty;
                    }

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
                case ConsoleKey.Backspace when currentInput.Length > 0:
                    lock (renderLock)
                    {
                        currentInput = currentInput[..^1];
                        ctx.UpdateTarget(BuildLayout());
                        ctx.Refresh();
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

        // Markup uses [/] to close the tag — always run user-controlled strings through
        // Markup.Escape so a speaker name containing '[' can't break the layout.
        Markup content = new(
            $"[bold {speakerColor}]{Markup.Escape(currentSpeaker)}[/]   " +
            $"[grey54]conv[/] [grey85]{Markup.Escape(shortId)}[/]");

        return new Panel(Align.Center(content, VerticalAlignment.Middle))
        {
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(Color.Grey50),
            // Panel title is static ("Rune") — it identifies the channel, not
            // the current speaker; that's what the body Markup above is for.
            Header = new PanelHeader($"[bold {MorganaColor}]Rune[/]"),
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

        int historyBudget = Math.Max(0, bodyHeight - inputRows.Count);
        List<IRenderable> rows = new(historyBudget + inputRows.Count);
        AppendHistoryTail(rows, termWidth, historyBudget);
        rows.AddRange(inputRows);
        return new Rows(rows);
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
                covered = budget;
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
    /// Number of terminal rows the rendered <c>"Who: Text"</c> spans at
    /// <paramref name="termWidth"/>. Splits the rendered string on embedded newlines
    /// first (Spectre's <see cref="Markup"/> honours <c>\n</c> as a hard row break,
    /// so an LLM reply with <c>...?\n\n#INT#</c> renders as three rows even if its
    /// total char count fits in one terminal row), then char-wraps each line at
    /// <paramref name="termWidth"/>. Empty messages and trailing empty lines still
    /// contribute one row each, matching what Spectre actually paints.
    /// </summary>
    private static int MessageWrapRowCount(DisplayedMessage message, int termWidth)
    {
        string fullText = $"{message.Who}: {message.Text}";
        int total = 0;
        foreach (string line in fullText.Split('\n'))
            total += line.Length == 0 ? 1 : (line.Length + termWidth - 1) / termWidth;
        return Math.Max(1, total);
    }

    /// <summary>
    /// Emits one <see cref="Markup"/> per wrap-row of <paramref name="message"/>, skipping
    /// the first <paramref name="skipRows"/> head rows. The entire message body is tinted
    /// in the speaker colour (magenta1 for Morgana, hotpink for specialised agents,
    /// skyblue1 for the user); the leading <c>Who:</c> prefix on the first row is the
    /// same colour but bold, so the speaker tag stands out without breaking colour
    /// continuity across wrap rows.
    /// </summary>
    /// <remarks>
    /// Mirrors <see cref="MessageWrapRowCount"/>: embedded newlines split the text into
    /// independent lines, each char-wrapped at <paramref name="termWidth"/>. Every
    /// emitted <see cref="Markup"/> is a single-line, single-row payload — no <c>\n</c>
    /// inside, no longer than <paramref name="termWidth"/> visible columns — so Spectre
    /// can never inflate it into multiple terminal rows behind the viewport budget's
    /// back. That contract is what keeps the sacred prompt and the conversation tail
    /// from being cropped by Spectre when a message with newlines slips into history.
    /// </remarks>
    private static void AppendMessageRows(List<IRenderable> output, DisplayedMessage message, int termWidth, int skipRows)
    {
        string fullText = $"{message.Who}: {message.Text}";

        int rowsSeen = 0;
        foreach (string line in fullText.Split('\n'))
        {
            int lineRows = line.Length == 0 ? 1 : (line.Length + termWidth - 1) / termWidth;
            for (int rowInLine = 0; rowInLine < lineRows; rowInLine++)
            {
                if (rowsSeen >= skipRows)
                {
                    int offset = rowInLine * termWidth;
                    int chunkLen = Math.Min(termWidth, Math.Max(0, line.Length - offset));
                    string chunk = chunkLen > 0 ? line.Substring(offset, chunkLen) : string.Empty;
                    EmitMessageRow(output, message, chunk, isFirstRowOfMessage: rowsSeen == 0);
                }
                rowsSeen++;
            }
        }
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
    /// Builds the sacred input/hint area as a list of pre-wrapped single-row markups.
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
        if (awaitingResponse)
        {
            string content = $"{currentSpeaker} is thinking…";
            return ChunkStyledRows(content, termWidth, "grey54 italic");
        }

        // Visible layout: position 0 = chevron, position 1 = space, positions 2..N+1 =
        // currentInput chars (N = currentInput.Length), position N+2 = cursor. Build the
        // markup row-by-row, applying the chevron / cursor style tags at their exact
        // positions so the cursor stays at the very end regardless of where the wrap
        // boundaries land.
        int totalLen = currentInput.Length + 3 /* "› " + "_" */;
        if (totalLen == 0)
            return [new Markup(string.Empty)];

        int cursorPos = totalLen - 1;
        List<IRenderable> rows = new();
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
    /// Mirrors Cauldron's ChatStateService.IsSpecializedAgent: a specialised agent
    /// announces itself as <c>Morgana (Intent)</c>, so the presence of parentheses
    /// is the discriminator between base Morgana and a domain agent.
    /// </summary>
    private static bool IsSpecializedAgent(string? agentName) =>
        agentName is not null && agentName.Contains('(') && agentName.Contains(')');

    /// <summary>A single history row: speaker name, rendered text, and the Spectre color token used for the name.</summary>
    private record DisplayedMessage(string Who, string Text, string Color);
}