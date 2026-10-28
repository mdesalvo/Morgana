using Morgana.Contracts;
using Morgana.Terminal.Abstractions;
using Morgana.Terminal.Interfaces;
using Morgana.Terminal.Messages;
using Morgana.Terminal.Services;
using Spectre.Console;

namespace Morgana.Terminal.Commands;

/// <summary><c>/status</c>: where the channel is connected and what state the conversation on screen is in.</summary>
public sealed class TerminalStatusCommand : TerminalCommand
{
    /// <summary>Secondary lines in the de-emphasised grey the palette uses for its descriptions.</summary>
    private const string DescriptionStyle = "grey70";

    /// <summary>A budget running low, in the amber the header's dust gauge turns at the same threshold.</summary>
    private const string LowStyle = "#f59e0b";

    /// <summary>A spent budget or a Morgana not answering, in the red the header's dust gauge turns when critical.</summary>
    private const string CriticalStyle = "#ef4444";

    /// <summary>Width of the label column: the longest label, so every value starts in one column.</summary>
    private const int LabelWidth = 12;

    /// <summary>Cells between a label and its value.</summary>
    private const int ColumnGap = 2;

    /// <summary>Below this many cells a value beside its label would be a column of stubs, so it goes under the label instead.</summary>
    private const int MinValueWidth = 16;

    /// <summary>Supplies the channel's identity and the capabilities it announced.</summary>
    private readonly ChannelProfile profile;

    /// <summary>The channel's Morgana colour and progress glyphs, which draw the labels and the dust bar.</summary>
    private readonly TerminalTheme theme;

    /// <summary>Measures and wraps every row to the width the UI's row budget counts on.</summary>
    private readonly TerminalCellService cells;

    /// <summary>Asks Morgana whether it answers and says where it is reached.</summary>
    private readonly MorganaClientService morganaClientService;

    /// <summary>Names the conversation on screen and when it opened.</summary>
    private readonly TerminalSessionService session;

    /// <summary>Captures the channel's identity and look, the REST client and the session.</summary>
    public TerminalStatusCommand(ChannelProfile profile, TerminalTheme theme, TerminalCellService cells, MorganaClientService morganaClientService, TerminalSessionService session)
    {
        this.profile = profile;
        this.theme = theme;
        this.cells = cells;
        this.morganaClientService = morganaClientService;
        this.session = session;
    }

    /// <inheritdoc />
    public override CommandDescriptor Descriptor { get; } = new("status", "Show the connection and the state of this conversation");

    /// <summary>A spent conversation is exactly when the user wants to see where it stands.</summary>
    public override bool AvailableWhenSpent => true;

    /// <inheritdoc />
    public override async Task ExecuteAsync(ITerminalUi ui, IReadOnlyDictionary<string, string> options, CancellationToken cancellationToken)
    {
        // Morgana not answering is the one thing that can make the user wait here, so the wait is named
        ui.ShowProgress(new CommandProgress(Descriptor.Name, "asking Morgana", Completed: 0, Total: 1));
        bool morganaHealthy = await morganaClientService.IsMorganaHealthyAsync(cancellationToken);

        // Read once the check is over, so the panel describes one moment of the session
        TerminalSessionStatus status = ui.Status;
        string conversationId = session.ConversationId;
        DateTimeOffset? openedAt = session.OpenedAt;
        ui.ShowPanel(width => LayOut(Math.Max(1, width), morganaHealthy, status, conversationId, openedAt));
    }

    /// <summary>The status as rows of at most <paramref name="width"/> cells: a titled rule, then one field per concern.</summary>
    private List<Markup> LayOut(int width, bool morganaHealthy, TerminalSessionStatus status, string conversationId, DateTimeOffset? openedAt)
    {
        List<Markup> rows = [];

        // The rule opens the panel in the channel's colour, as /help's does
        rows.Add(cells.RenderPanelTitle($"{profile.DisplayName} status", width, theme.MorganaColor));

        // Where Morgana is reached, shown without the trailing slash the base address carries
        string morganaAddress = morganaClientService.MorganaAddress?.ToString().TrimEnd('/') ?? "no address configured";
        AddField(rows, width, "Morgana", morganaHealthy
            ? [new($"{morganaAddress} · reachable", "default")]
            : [new($"{morganaAddress} · not answering", CriticalStyle)]);

        // The channel's identity, where its replies land and what it was promised at the handshake
        AddField(rows, width, "Channel",
        [
            new($"{profile.ChannelName} · replies by webhook to {morganaClientService.CallbackUrl}", "default"),
            new(DescribeCapabilities(profile.Capabilities), DescriptionStyle)
        ]);

        // The count is spelled for the singular
        string messages = status.MessageCount == 1 ? "1 message" : $"{status.MessageCount} messages";
        AddField(rows, width, "Conversation",
        [
            new(conversationId, "default"),
            new(openedAt is { } opened ? $"opened {opened:HH:mm} · {messages}" : messages, DescriptionStyle)
        ]);

        // Who holds the conversation now, as the header shows it
        AddField(rows, width, "Talking to", [new(status.Speaker, "default")]);
        AddField(rows, width, "Magic dust", DescribeDust(status));
        return rows;
    }

    /// <summary>What the channel was promised at the handshake, told as what the user will and will not see.</summary>
    private static string DescribeCapabilities(ChannelCapabilities capabilities)
    {
        List<string> features = [];
        if (capabilities.SupportsRichCards)
            features.Add("rich cards");
        if (capabilities.SupportsQuickReplies)
            features.Add("quick replies");
        if (capabilities.SupportsStreaming)
            features.Add("streaming");
        if (capabilities.SupportsMarkdown)
            features.Add("markdown");

        // A capped channel is where a newcomer wonders why replies are short, so the cap is said in their terms
        string length = capabilities.MaxMessageLength is { } cap
            ? $"Morgana fits every reply into {cap} characters"
            : "no length limit";
        return $"{(features.Count == 0 ? "plain text" : string.Join(", ", features))} · {length}";
    }

    /// <summary>The budget as a percentage and a bar in the header's colours, then what running out means.</summary>
    private List<StatusLine> DescribeDust(TerminalSessionStatus status)
    {
        if (status.Spent)
            return [new("spent", CriticalStyle), new("this conversation is over: /new starts a fresh one", DescriptionStyle)];

        // Morgana reports the level with its first reply; until then, or on a Morgana limiting nothing, there is none
        if (status.DustLevel is not { } level)
            return [new("not reported yet", DescriptionStyle)];

        // Truncated as the header truncates it, so the two never show different numbers; the bar alike, so a
        // budget already dented never draws as full
        int percent = Math.Clamp((int)(level * 100), 0, 100);
        int filledCells = (int)(Math.Clamp(level, 0, 1) * theme.ProgressWidth);
        string bar = new string(theme.ProgressFilledGlyph, filledCells) + new string(theme.ProgressEmptyGlyph, theme.ProgressWidth - filledCells);
        string style = level > 0.30 ? theme.MorganaColor : level > 0.10 ? LowStyle : CriticalStyle;
        return [new($"{percent}% left {bar}", style), new("at 0% this conversation stops: /new starts a fresh one", DescriptionStyle)];
    }

    /// <summary>Adds one field: its label in the channel's colour, then each value line wrapped under its own indent.</summary>
    private void AddField(List<Markup> rows, int width, string label, IReadOnlyList<StatusLine> lines)
    {
        int valueWidth = width - LabelWidth - ColumnGap;
        bool besideLabel = valueWidth >= MinValueWidth;
        string labelCell = $"[{theme.MorganaColor} bold]{Markup.Escape(label.PadRight(LabelWidth + ColumnGap))}[/]";
        string indent = new(' ', besideLabel ? LabelWidth + ColumnGap : ColumnGap);

        // A terminal too narrow for two columns gets the label on a row of its own and the values under it
        if (!besideLabel)
        {
            // The label takes a row of its own
            rows.Add(new Markup($"[{theme.MorganaColor} bold]{Markup.Escape(cells.Trunc(label, width))}[/]"));
            valueWidth = Math.Max(1, width - ColumnGap);
        }

        // The label cell leads the first row of the field and the indent the others
        bool firstRow = besideLabel;
        foreach (StatusLine line in lines)
        {
            // Each line is wrapped at the spaces under the width left for values
            foreach (string piece in cells.WrapWords(line.Text, valueWidth))
            {
                // Rows beside the label carry it only on the first row
                rows.Add(new Markup($"{(firstRow ? labelCell : indent)}[{line.Style}]{Markup.Escape(piece)}[/]"));
                firstRow = false;
            }
        }
    }
}
