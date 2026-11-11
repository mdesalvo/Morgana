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
    /// <summary>The style of descriptions and key actions, the dimmed grey the palette uses for its descriptions.</summary>
    private const string DescriptionStyle = "grey70";

    /// <summary>The style of separators and the palette hint, the grey the palette uses for its controls line.</summary>
    private const string HintStyle = "grey54 italic";

    /// <summary>The cells between a command's name column and its description.</summary>
    private const int ColumnGap = 2;

    /// <summary>The narrowest description drawn beside its name; below it the entry is cut to one row instead of a column of stubs.</summary>
    private const int MinDescriptionWidth = 16;

    /// <summary>Supplies the channel's introduction and key bindings, the part of the help no other channel shares.</summary>
    private readonly ChannelProfile profile;

    /// <summary>The channel's Morgana colour, which marks the title, the section heading and every command name.</summary>
    private readonly TerminalTheme theme;

    /// <summary>Measures and wraps every row to the width that the UI's row budget counts on.</summary>
    private readonly TerminalCellService cells;

    /// <summary>Builds the command on the channel's identity, its theme and the cell measurement.</summary>
    public TerminalHelpCommand(ChannelProfile profile, TerminalTheme theme, TerminalCellService cells)
    {
        // The help is written from the channel's profile, painted in its theme and measured with the UI's cells.
        this.profile = profile;
        this.theme = theme;
        this.cells = cells;

        // The channel is named in the description, so the palette says what is being explained.
        Descriptor = new CommandDescriptor("help", $"Explain {profile.DisplayName} and list its commands");
    }

    /// <inheritdoc />
    public override CommandDescriptor Descriptor { get; }

    /// <summary>True, because a spent conversation is where a newcomer most needs to learn the way out of it.</summary>
    public override bool AvailableWhenSpent => true;

    /// <inheritdoc />
    public override Task ExecuteAsync(ITerminalUi ui, IReadOnlyDictionary<string, string> options, CancellationToken cancellationToken)
    {
        // The palette's own list at this moment, so nothing is described that the user could not then pick
        IReadOnlyList<CommandDescriptor> commands = ui.AvailableCommands;

        // The panel is laid out again at every frame's width, so a resize never cuts it.
        ui.ShowPanel(width => LayOut(commands, Math.Max(1, width)));
        return Task.CompletedTask;
    }

    /// <summary>The help as rows of at most <paramref name="width"/> cells: a titled rule, the introduction, the keys, then the commands.</summary>
    private List<Markup> LayOut(IReadOnlyList<CommandDescriptor> commands, int width)
    {
        // The rows of the panel, top to bottom.
        List<Markup> rows = [];

        // The rule opens the block in the channel's colour, so the help reads apart from the transcript above it
        rows.Add(cells.RenderPanelTitle($"{profile.DisplayName} help", width, theme.MorganaColor));

        // The introduction says what the channel is and how talking through it goes, wrapped at the spaces
        rows.AddRange(cells.WrapWords(profile.Introduction, width).Select(row => new Markup(Markup.Escape(row))));

        // The keys follow, since they are how everything else is reached.
        rows.AddRange(LayOutKeyBindings(width));

        // The heading carries how the list is reached, which is the one thing a newcomer does with it
        const string heading = "Commands";
        string paletteHint = cells.Trunc("type / to open them · Tab completes · Enter runs", Math.Max(1, width - heading.Length - ColumnGap));
        rows.Add(new Markup($"[{theme.MorganaColor} bold]{heading}[/]{new string(' ', ColumnGap)}[{HintStyle}]{Markup.Escape(paletteHint)}[/]"));
        rows.AddRange(LayOutCommands(commands, width));

        // The whole help, ready for the panel.
        return rows;
    }

    /// <summary>The key bindings packed into as few rows as fit, each binding kept whole on one row.</summary>
    private List<Markup> LayOutKeyBindings(int width)
    {
        // Bindings on one row are told apart by a dimmed dot; the rows closed so far and the one being filled.
        const string separator = " · ";
        List<Markup> rows = [];
        StringBuilder row = new();
        int rowCells = 0;

        // Each binding is measured whole, because a binding is never split across two rows
        foreach ((string keys, string action) in profile.KeyBindings)
        {
            // Keys, one space and action are the cells the binding occupies
            int bindingCells = keys.GetCellWidth() + 1 + action.GetCellWidth();

            // A binding wider than the whole row is cut as plain text, since keys and action cannot both be kept
            string binding = bindingCells <= width
                ? $"[bold]{Markup.Escape(keys)}[/] [{DescriptionStyle}]{Markup.Escape(action)}[/]"
                : $"[{DescriptionStyle}]{Markup.Escape(cells.Trunc($"{keys} {action}", width))}[/]";
            bindingCells = Math.Min(bindingCells, width);

            // A binding that does not fit after the separator opens the next row
            if (rowCells > 0 && rowCells + separator.Length + bindingCells > width)
            {
                // The row is closed with the bindings it holds
                rows.Add(new Markup(row.ToString()));
                row.Clear();
                rowCells = 0;
            }

            // Bindings on the same row are told apart by a separator
            if (rowCells > 0)
            {
                // The separator is dimmed so the bindings read as the content
                row.Append($"[{HintStyle}]{separator}[/]");
                rowCells += separator.Length;
            }

            // The binding joins the row being built
            row.Append(binding);
            rowCells += bindingCells;
        }

        // The last row holds the bindings left
        if (rowCells > 0)
            rows.Add(new Markup(row.ToString()));

        // Every binding, packed into as few rows as fit.
        return rows;
    }

    /// <summary>One entry per command: the name in a column of its own, the description wrapped beside it under its own indent.</summary>
    private List<Markup> LayOutCommands(IReadOnlyList<CommandDescriptor> commands, int width)
    {
        // The entries sit two cells in, under the heading; the rows filled so far.
        const int indent = 2;
        List<Markup> rows = [];

        // No command is available, so the heading stands alone.
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

        // The name column is as wide as the longest name, so every description starts at the same cell
        int labelWidth = entries.Max(entry => entry.Label.GetCellWidth());
        int descriptionWidth = width - indent - labelWidth - ColumnGap;

        // Each command is one entry of the list
        foreach ((string label, string description) in entries)
        {
            // The label is padded to the column so descriptions line up
            string paddedLabel = new string(' ', indent) + label + new string(' ', labelWidth - label.GetCellWidth() + ColumnGap);

            // A terminal too narrow for two columns keeps the name and as much of the description as the row holds
            if (descriptionWidth < MinDescriptionWidth)
            {
                // The entry is drawn as one truncated row in the channel's colour
                rows.Add(new Markup($"[{theme.MorganaColor}]{Markup.Escape(cells.Trunc($"  {label}  {description}", width))}[/]"));
                continue;
            }

            // Continuation rows start under the description, so the name column stays clear down the whole list
            List<string> descriptionRows = cells.WrapWords(description, descriptionWidth);
            rows.Add(new Markup($"[{theme.MorganaColor}]{Markup.Escape(paddedLabel)}[/][{DescriptionStyle}]{Markup.Escape(descriptionRows[0])}[/]"));
            rows.AddRange(descriptionRows.Skip(1).Select(continuation =>
                new Markup($"{new string(' ', paddedLabel.Length)}[{DescriptionStyle}]{Markup.Escape(continuation)}[/]")));
        }

        // The rows the panel draws below the heading
        return rows;
    }
}