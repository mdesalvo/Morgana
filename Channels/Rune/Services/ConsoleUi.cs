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
public sealed class ConsoleUi
{
    private const string MorganaColor = "magenta1";
    private const string MorganaAgentColor = "hotpink";
    private const string UserColor = "skyblue1";
    private const string DefaultAgentExitMessage = "{0} has completed its spell. I'm back to you!";

    private readonly List<DisplayedMessage> history = [];
    private readonly Channel<ChannelMessage> incoming = Channel.CreateUnbounded<ChannelMessage>();
    private readonly string agentExitTemplate;
    private string currentInput = string.Empty;
    private string currentSpeaker = "Morgana";
    private string conversationId = string.Empty;
    private volatile bool exitRequested;

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

        await AnsiConsole.Live(layout)
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

    private async Task DrainIncomingLoop(LiveDisplayContext ctx, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (ChannelMessage message in incoming.Reader.ReadAllAsync(cancellationToken))
            {
                // The message itself is attributed to whoever authored it (a specialised
                // agent keeps its own colour on its last line, including the farewell
                // that carries AgentCompleted=true). After appending it, if that agent
                // just signalled completion, follow up with a base-Morgana courtesy line
                // — same pattern Cauldron's ChatStateService.AddCompletionMessageIfNeeded
                // uses — and revert the sticky header speaker to Morgana so the next
                // user turn doesn't render under the outgoing agent's colour.
                string messageSpeaker = string.IsNullOrWhiteSpace(message.AgentName) ? "Morgana" : message.AgentName;
                history.Add(new DisplayedMessage(messageSpeaker, message.Text, SpeakerColor(messageSpeaker)));

                if (message.AgentCompleted && IsSpecializedAgent(message.AgentName))
                {
                    string completion = string.Format(agentExitTemplate, message.AgentName);
                    history.Add(new DisplayedMessage("Morgana", completion, MorganaColor));
                }

                currentSpeaker = message.AgentCompleted || string.IsNullOrWhiteSpace(message.AgentName)
                    ? "Morgana"
                    : message.AgentName;

                ctx.UpdateTarget(BuildLayout());
                ctx.Refresh();

                if (exitRequested)
                    return;
            }
        }
        catch (OperationCanceledException)
        {
            // Cancellation is the normal exit path — swallow.
        }
    }

    private async Task ReadKeysLoop(LiveDisplayContext ctx, Func<string, Task> onSend, CancellationToken cancellationToken)
    {
        while (!exitRequested && !cancellationToken.IsCancellationRequested)
        {
            if (!Console.KeyAvailable)
            {
                await Task.Delay(25, cancellationToken);
                continue;
            }

            ConsoleKeyInfo key = Console.ReadKey(intercept: true);
            switch (key.Key)
            {
                case ConsoleKey.Enter when currentInput.Length > 0:
                {
                    string toSend = currentInput;
                    currentInput = string.Empty;

                    if (toSend.Equals("/quit", StringComparison.OrdinalIgnoreCase))
                    {
                        exitRequested = true;
                        incoming.Writer.TryComplete();
                        return;
                    }

                    history.Add(new DisplayedMessage("You", toSend, UserColor));
                    ctx.UpdateTarget(BuildLayout());
                    ctx.Refresh();

                    try
                    {
                        await onSend(toSend);
                    }
                    catch (Exception ex)
                    {
                        history.Add(new DisplayedMessage("system", $"send failed: {ex.Message}", "red"));
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
                    exitRequested = true;
                    incoming.Writer.TryComplete();
                    return;
                default:
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

    private IRenderable BuildHeader()
    {
        string speakerColor = SpeakerColor(currentSpeaker);
        string shortId = conversationId.Length > 12 ? conversationId[..12] + "…" : conversationId;

        Markup content = new(
            $"[bold {speakerColor}]{Markup.Escape(currentSpeaker)}[/]   " +
            $"[grey54]conv[/] [grey85]{Markup.Escape(shortId)}[/]");

        return new Panel(Align.Center(content, VerticalAlignment.Middle))
        {
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(Color.Grey50),
            Header = new PanelHeader($"[bold {MorganaColor}]Rune[/] [grey54]→[/] [bold]Morgana[/]"),
            Padding = new Padding(1, 0, 1, 0),
            Expand = true
        };
    }

    private IRenderable BuildBody()
    {
        List<IRenderable> rows = new(capacity: history.Count + 1);
        foreach (DisplayedMessage message in history)
            rows.Add(new Markup($"[bold {message.Color}]{Markup.Escape(message.Who)}:[/] {Markup.Escape(message.Text)}"));

        rows.Add(new Markup($"[{UserColor}]›[/] {Markup.Escape(currentInput)}[blink {UserColor}]_[/]"));
        return new Rows(rows);
    }

    private static string SpeakerColor(string agentName)
    {
        if (agentName.Equals("You", StringComparison.OrdinalIgnoreCase)) return UserColor;
        if (agentName.Equals("Morgana", StringComparison.OrdinalIgnoreCase)) return MorganaColor;
        return MorganaAgentColor;
    }

    /// <summary>
    /// Mirrors Cauldron's ChatStateService.IsSpecializedAgent: a specialised agent
    /// announces itself as <c>Morgana (Something)</c>, so the presence of parentheses
    /// is the discriminator between base Morgana and a domain agent.
    /// </summary>
    private static bool IsSpecializedAgent(string? agentName) =>
        agentName is not null && agentName.Contains('(') && agentName.Contains(')');

    private record DisplayedMessage(string Who, string Text, string Color);
}