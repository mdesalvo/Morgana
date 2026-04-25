using System.Threading.Channels;
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
public sealed class ConsoleUi
{
    /// <summary>Color for base Morgana turns.</summary>
    private const string MorganaColor = "magenta1";

    /// <summary>Color for specialised agent turns (<c>Morgana (Something)</c>).</summary>
    private const string MorganaAgentColor = "hotpink";

    /// <summary>Color for the user's own input and committed lines.</summary>
    private const string UserColor = "skyblue1";

    /// <summary>Fallback for <c>Rune:AgentExitMessage</c> when the setting is absent.</summary>
    private const string DefaultAgentExitMessage = "{0} has completed its spell. I'm back to you!";

    /// <summary>Chat history rendered in the scrolling body; trimmed to viewport on each update.</summary>
    private readonly List<DisplayedMessage> history = [];

    /// <summary>Thread-safe queue of messages posted by <see cref="WebhookReceiver"/> awaiting render.</summary>
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

    /// <summary>Reads the <c>Rune:AgentExitMessage</c> template, falling back to <see cref="DefaultAgentExitMessage"/>.</summary>
    public ConsoleUi(IConfiguration configuration)
    {
        agentExitTemplate = configuration["Rune:AgentExitMessage"] ?? DefaultAgentExitMessage;
    }

    /// <summary>Called by <see cref="WebhookReceiver"/> when Morgana delivers a message.</summary>
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
                TrimHistoryToViewport();
                ctx.UpdateTarget(BuildLayout());
                ctx.Refresh();

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
                    string toSend = currentInput;
                    currentInput = string.Empty;

                    // /quit is a client-only command: never round-trip it to Morgana.
                    if (toSend.Equals("/quit", StringComparison.OrdinalIgnoreCase))
                    {
                        exitRequested = true;
                        incoming.Writer.TryComplete();
                        return;
                    }

                    // Optimistic echo: show "You: …" and flip to the thinking hint before
                    // awaiting onSend so the UI feels responsive even on slow backends.
                    history.Add(new DisplayedMessage("You", toSend, UserColor));
                    awaitingResponse = true;
                    TrimHistoryToViewport();
                    ctx.UpdateTarget(BuildLayout());
                    ctx.Refresh();

                    try
                    {
                        await onSend(toSend);
                    }
                    catch (Exception ex)
                    {
                        // Surface the failure in-UI (logging is silenced) and release the
                        // gate so the user can retry without waiting for a webhook that
                        // will never arrive.
                        history.Add(new DisplayedMessage("system", $"send failed: {ex.Message}", "red"));
                        awaitingResponse = false;
                        TrimHistoryToViewport();
                        ctx.UpdateTarget(BuildLayout());
                        ctx.Refresh();
                    }
                    break;
                }
                case ConsoleKey.Backspace when currentInput.Length > 0:
                    currentInput = currentInput[..^1];
                    ctx.UpdateTarget(BuildLayout());
                    ctx.Refresh();
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
                        currentInput += key.KeyChar;
                        ctx.UpdateTarget(BuildLayout());
                        ctx.Refresh();
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
            // Panel title is static ("Rune → Morgana") — it identifies the channel, not
            // the current speaker; that's what the body Markup above is for.
            Header = new PanelHeader($"[bold {MorganaColor}]Rune[/] [grey54]→[/] [bold]Morgana[/]"),
            Padding = new Padding(1, 0, 1, 0),
            Expand = true
        };
    }

    /// <summary>Renders the chat history followed by either a thinking hint (gated) or the input line with a blinking cursor.</summary>
    private IRenderable BuildBody()
    {
        // Pre-size for history + the single trailing input/hint row — saves a realloc on
        // every keystroke-driven repaint.
        List<IRenderable> rows = new(capacity: history.Count + 1);

        // Render history in order; each row is bold-colored "Who:" followed by the text.
        // TrimHistoryToViewport has already dropped anything that wouldn't fit.
        foreach (DisplayedMessage message in history)
            rows.Add(new Markup($"[bold {message.Color}]{Markup.Escape(message.Who)}:[/] {Markup.Escape(message.Text)}"));

        // The last row is mutually exclusive: thinking hint while gated, prompt line
        // otherwise. The blink tag on the underscore is what draws the cursor — Spectre
        // has no first-class input widget we can borrow here.
        rows.Add(awaitingResponse
            ? new Markup($"[grey54 italic]{Markup.Escape(currentSpeaker)} is thinking…[/]")
            : new Markup($"[{UserColor}]›[/] {Markup.Escape(currentInput)}[blink {UserColor}]_[/]"));
        return new Rows(rows);
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

    /// <summary>
    /// Drops the oldest entries from <see cref="history"/> until the list fits inside
    /// the current body viewport. Called before every <see cref="BuildLayout"/> on the
    /// same thread that appends new entries — the list is not shared across threads
    /// beyond that discipline. Recomputes the terminal dimensions on each call so the
    /// trim follows live resizes without extra plumbing.
    /// </summary>
    /// <remarks>
    /// Budget accounting:
    /// <list type="bullet">
    /// <item>Header panel occupies 3 rows (fixed via <c>SplitRows(header.Size(3))</c>).</item>
    /// <item>Input line is the last <see cref="Rows"/> child — reserve 1 row for it.</item>
    /// <item>Each history entry is rendered as <c>"Who: text"</c> and wraps at the
    /// terminal width; row cost is <c>ceil(rendered / width)</c>, floored to 1.</item>
    /// </list>
    /// Walking the list from the tail backwards we keep the most recent entries that
    /// still fit within the body budget and discard the rest. The trim is destructive:
    /// discarded messages are unrecoverable — Rune's UI is a fixed-viewport renderer
    /// without scrollback, and this method makes that choice explicit in <c>history</c>
    /// rather than leaving it to Spectre's <c>VerticalOverflowCropping.Top</c>.
    /// </remarks>
    private void TrimHistoryToViewport()
    {
        int bodyRows = Math.Max(1, Console.WindowHeight - 3 /* header */ - 1 /* input */);
        int termWidth = Math.Max(20, Console.WindowWidth);

        int totalRows = 0;
        int firstKept = history.Count;
        for (int i = history.Count - 1; i >= 0; i--)
        {
            DisplayedMessage message = history[i];
            int rendered = message.Who.Length + 2 /* ": " */ + message.Text.Length;
            int rows = Math.Max(1, (rendered + termWidth - 1) / termWidth);
            if (totalRows + rows > bodyRows)
                break;
            totalRows += rows;
            firstKept = i;
        }

        if (firstKept > 0)
            history.RemoveRange(0, firstKept);
    }

    /// <summary>A single history row: speaker name, rendered text, and the Spectre color token used for the name.</summary>
    private record DisplayedMessage(string Who, string Text, string Color);
}