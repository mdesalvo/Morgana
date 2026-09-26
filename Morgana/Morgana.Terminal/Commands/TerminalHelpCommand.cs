using System.Text;
using Morgana.Contracts;
using Morgana.Terminal.Abstractions;
using Morgana.Terminal.Interfaces;
using Morgana.Terminal.Messages;
using Morgana.Terminal.Services;
using Spectre.Console;

namespace Morgana.Terminal.Commands;

/// <summary><c>/help</c>: tells a newcomer what the channel is, which keys drive it and which commands they can run.</summary>
public sealed class TerminalHelpCommand : TerminalCommand
{
    /// <summary>Descriptions and key actions in the de-emphasised grey the palette uses for its descriptions.</summary>
    private const string DescriptionStyle = "grey70";

    /// <summary>Separators and the palette hint in the grey the palette uses for its controls line.</summary>
    private const string HintStyle = "grey54 italic";

    /// <summary>Cells between a command's name column and its description.</summary>
    private const int ColumnGap = 2;

    /// <summary>Below this many cells a description beside its name would be a column of stubs, so it is cut instead.</summary>
    private const int MinDescriptionWidth = 16;

    /// <summary>Supplies the channel's introduction and key bindings, the part of the help no other channel shares.</summary>
    private readonly ChannelProfile profile;

    /// <summary>The channel's primary colour, which marks the title, the section heading and every command name.</summary>
    private readonly CommandTheme theme;

    /// <summary>Measures and wraps every row to the width the UI's row budget counts on.</summary>
    private readonly TerminalCellService cells;

    /// <summary>Names the channel in the description, so the palette says what is being explained.</summary>
    public TerminalHelpCommand(ChannelProfile profile, CommandTheme theme, TerminalCellService cells)
    {
        this.profile = profile;
        this.theme = theme;
        this.cells = cells;
        Descriptor = new CommandDescriptor("help", $"Explain {profile.DisplayName} and list its commands");
    }

    /// <inheritdoc />
    public override CommandDescriptor Descriptor { get; }

    /// <summary>A spent conversation is where a newcomer most needs to learn the way out of it.</summary>
    public override bool AvailableWhenSpent => true;

    /// <inheritdoc />
    public override Task ExecuteAsync(ITerminalUi ui, IReadOnlyDictionary<string, string> options, CancellationToken cancellationToken)
    {
        // The palette's own list at this moment, so nothing is described that the user could not then pick
        IReadOnlyList<CommandDescriptor> commands = ui.AvailableCommands;
        ui.ShowPanel(width => LayOut(commands, Math.Max(1, width)));
        return Task.CompletedTask;
    }

    /// <summary>The help as rows of at most <paramref name="width"/> cells: a titled rule, the introduction, the keys, then the commands.</summary>
    private List<Markup> LayOut(IReadOnlyList<CommandDescriptor> commands, int width)
    {
        List<Markup> rows = [];

        // The rule opens the block in the channel's colour, so the help reads apart from the transcript above it
        string title = $"── {profile.DisplayName} help ";
        rows.Add(new Markup($"[{theme.PrimaryColor}]{Markup.Escape(cells.Trunc(title + new string('─', Math.Max(0, width - title.GetCellWidth())), width))}[/]"));

        rows.AddRange(cells.WrapWords(profile.Introduction, width).Select(row => new Markup(Markup.Escape(row))));
        rows.AddRange(LayOutKeyBindings(width));

        // The heading carries how the list is reached, which is the one thing a newcomer does with it
        const string heading = "Commands";
        string paletteHint = cells.Trunc("type / to open them · Tab completes · Enter runs", Math.Max(1, width - heading.Length - ColumnGap));
        rows.Add(new Markup($"[{theme.PrimaryColor} bold]{heading}[/]{new string(' ', ColumnGap)}[{HintStyle}]{Markup.Escape(paletteHint)}[/]"));
        rows.AddRange(LayOutCommands(commands, width));
        return rows;
    }

    /// <summary>The key bindings packed into as few rows as fit, each binding kept whole on one row.</summary>
    private List<Markup> LayOutKeyBindings(int width)
    {
        const string separator = " · ";
        List<Markup> rows = [];
        StringBuilder row = new();
        int rowCells = 0;

        foreach ((string keys, string action) in profile.KeyBindings)
        {
            int bindingCells = keys.GetCellWidth() + 1 + action.GetCellWidth();

            // A binding wider than the whole row is cut as plain text, since keys and action cannot both be kept
            string binding = bindingCells <= width
                ? $"[bold]{Markup.Escape(keys)}[/] [{DescriptionStyle}]{Markup.Escape(action)}[/]"
                : $"[{DescriptionStyle}]{Markup.Escape(cells.Trunc($"{keys} {action}", width))}[/]";
            bindingCells = Math.Min(bindingCells, width);

            if (rowCells > 0 && rowCells + separator.Length + bindingCells > width)
            {
                rows.Add(new Markup(row.ToString()));
                row.Clear();
                rowCells = 0;
            }

            if (rowCells > 0)
            {
                row.Append($"[{HintStyle}]{separator}[/]");
                rowCells += separator.Length;
            }

            row.Append(binding);
            rowCells += bindingCells;
        }

        if (rowCells > 0)
            rows.Add(new Markup(row.ToString()));
        return rows;
    }

    /// <summary>One entry per command: the name in a column of its own, the description wrapped beside it under its own indent.</summary>
    private List<Markup> LayOutCommands(IReadOnlyList<CommandDescriptor> commands, int width)
    {
        const int indent = 2;
        List<Markup> rows = [];
        if (commands.Count == 0)
            return rows;

        // Morgana's names and descriptions arrive from its catalogue, so they are drawn only once they are plain text
        List<(string Label, string Description)> entries =
        [
            .. commands.Select(command => (
                Label: "/" + TerminalCellService.StripControlCharacters(command.Name),

                // A command that ends something asks first, which a newcomer is better told than surprised by
                Description: TerminalCellService.StripControlCharacters(command.Description) + (command.RequiresConfirmation ? " · asks first" : string.Empty)))
        ];

        int labelWidth = entries.Max(entry => entry.Label.GetCellWidth());
        int descriptionWidth = width - indent - labelWidth - ColumnGap;

        foreach ((string label, string description) in entries)
        {
            string paddedLabel = new string(' ', indent) + label + new string(' ', labelWidth - label.GetCellWidth() + ColumnGap);

            // A terminal too narrow for two columns keeps the name and as much of the description as the row holds
            if (descriptionWidth < MinDescriptionWidth)
            {
                rows.Add(new Markup($"[{theme.PrimaryColor}]{Markup.Escape(cells.Trunc($"  {label}  {description}", width))}[/]"));
                continue;
            }

            // Continuation rows start under the description, so the name column stays clear down the whole list
            List<string> descriptionRows = cells.WrapWords(description, descriptionWidth);
            rows.Add(new Markup($"[{theme.PrimaryColor}]{Markup.Escape(paddedLabel)}[/][{DescriptionStyle}]{Markup.Escape(descriptionRows[0])}[/]"));
            rows.AddRange(descriptionRows.Skip(1).Select(continuation =>
                new Markup($"{new string(' ', paddedLabel.Length)}[{DescriptionStyle}]{Markup.Escape(continuation)}[/]")));
        }

        return rows;
    }
}
