using System.Text;
using Morgana.Contracts;
using Morgana.Terminal.Messages;
using Spectre.Console;

namespace Morgana.Terminal.Services;

/// <summary>The dropdown that opens under the prompt as soon as the line starts with <c>/</c>, listing the commands that match it.</summary>
// It decides which commands match, which one is highlighted, what Tab completes to and what Enter runs. It is drawn in the
// channel's own TerminalTheme. It holds the highlight, so every call comes under the render lock of the UI that owns it.
public sealed class CommandPaletteService
{
    /// <summary>How many commands are listed at once; the window follows the highlight past it.</summary>
    private const int MaxVisibleCommands = 6;

    /// <summary>The controls line, since a terminal gives no cue that a list can be walked.</summary>
    private const string ControlsHint = "↑↓ move · Tab complete · Enter run · Esc cancel";

    /// <summary>The colour of the names that are not highlighted, leaving the channel's Morgana colour to the one that is.</summary>
    private const string CandidateNameColor = "white";

    /// <summary>The style of the descriptions, the dimmed grey the other TTY surfaces use.</summary>
    private const string DescriptionStyle = "grey70";

    /// <summary>The style of the controls line.</summary>
    private const string HintStyle = "grey54 italic";

    /// <summary>Supplies the commands to match.</summary>
    private readonly TerminalCommandRegistryService registry;

    /// <summary>Measures and cuts every row to the width that the UI's row budget counts on.</summary>
    private readonly TerminalCellService cells;

    /// <summary>The channel's Morgana colour, which marks the highlighted command.</summary>
    private readonly TerminalTheme theme;

    /// <summary>The line the highlight was placed against; any other line puts it back on the first match.</summary>
    private string highlightedForInput = string.Empty;

    /// <summary>The position of the highlighted command among the commands matching <see cref="highlightedForInput"/>.</summary>
    private int highlightedCommandIndex;

    /// <summary>Builds the palette on the registry, the cell measurement and the channel's theme.</summary>
    public CommandPaletteService(TerminalCommandRegistryService registry, TerminalCellService cells, TerminalTheme theme)
    {
        // The registry supplies the commands, which the palette measures with the UI's cells and paints in the channel's theme.
        this.registry = registry;
        this.cells = cells;
        this.theme = theme;
    }

    /// <summary>Tells whether <paramref name="input"/> is a command line, which a slash in first position makes it.</summary>
    public static bool IsCommandLine(string input) => input.StartsWith('/');

    /// <summary>Moves the highlight by <paramref name="rowOffset"/> rows, wrapping at both ends.</summary>
    public void MoveHighlight(string input, TerminalConversationState state, int rowOffset)
    {
        // The highlight walks the list the user is looking at, the commands that the typed name still matches.
        IReadOnlyList<CommandDescriptor> matchingCommands = FindMatchingCommands(input, state);
        if (matchingCommands.Count == 0)
            return;

        // Moving up from the first command lands on the last one and down from the last on the first.
        int currentIndex = HighlightedIndexFor(input, matchingCommands.Count);
        highlightedCommandIndex = ((currentIndex + rowOffset) % matchingCommands.Count + matchingCommands.Count) % matchingCommands.Count;
    }

    /// <summary>The line that Tab turns <paramref name="input"/> into, the highlighted command spelled out; null when no command matches.</summary>
    public string? CompleteHighlightedCommand(string input, TerminalConversationState state)
    {
        // Tab completes to the highlighted row, the one the user sees selected, not necessarily the first.
        IReadOnlyList<CommandDescriptor> matchingCommands = FindMatchingCommands(input, state);

        // A line matching nothing completes to nothing.
        if (matchingCommands.Count == 0)
            return null;

        // Only the name is rewritten, so values already written stay as they were typed.
        CommandDescriptor highlightedCommand = matchingCommands[HighlightedIndexFor(input, matchingCommands.Count)];
        int optionsStart = input.IndexOf(' ');
        if (optionsStart >= 0)
            return $"/{highlightedCommand.Name}{input[optionsStart..]}";

        // A command that takes values leaves the caret past a space where the first one goes.
        return highlightedCommand.Options is { Count: > 0 } ? $"/{highlightedCommand.Name} " : $"/{highlightedCommand.Name}";
    }

    /// <summary>The command that Enter runs for <paramref name="input"/>; null when nothing matches it.</summary>
    public CommandDescriptor? ResolveCommandToRun(string input, TerminalConversationState state)
    {
        // A line matching nothing runs nothing.
        IReadOnlyList<CommandDescriptor> matchingCommands = FindMatchingCommands(input, state);
        if (matchingCommands.Count == 0)
            return null;

        // Enter runs the highlighted row: the first match until the arrows move to another.
        return matchingCommands[HighlightedIndexFor(input, matchingCommands.Count)];
    }

    /// <summary>The colour of a match, the channel's Morgana colour: the highlighted command and a typed line naming a command exactly.</summary>
    public string MatchColor => theme.MorganaColor;

    /// <summary>Tells whether <paramref name="input"/> names an available command exactly.</summary>
    public bool NamesCommandExactly(string input, TerminalConversationState state)
    {
        // The line is then drawn in the match colour, because what was typed is a command in itself.
        string typedCommandName = CommandLineParser.ParseName(input);
        return IsCommandLine(input) && registry.ListAvailableCommands(state)
            .Any(command => string.Equals(command.Name, typedCommandName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The palette for <paramref name="input"/> as rows of at most <paramref name="width"/> cells: the matching commands, then the controls line.</summary>
    // Every row is one markup, so the UI budgets them against its history.
    public List<Markup> RenderPalette(string input, TerminalConversationState state, int width)
    {
        // A terminal reporting no width still gets one cell per row.
        width = Math.Max(1, width);

        // A line without the slash has no palette and the highlight is forgotten with it: deleting the slash and typing it
        // again starts from the first command.
        if (!IsCommandLine(input))
        {
            // The highlight is forgotten together with the palette.
            highlightedForInput = string.Empty;
            return [];
        }

        // The line typed is the user's own, but it is echoed back through Markup, so it is cleaned all the same.
        IReadOnlyList<CommandDescriptor> matchingCommands = FindMatchingCommands(input, state);
        if (matchingCommands.Count == 0)
            return [new Markup($"[{DescriptionStyle}]{Markup.Escape(cells.Trunc(cells.SanitizeForTerminal($"no command matches {input}"), width))}[/]")];

        // The window ends on the highlight once it moves past the last visible row, so the highlight is always on screen.
        int highlightedIndex = HighlightedIndexFor(input, matchingCommands.Count);
        int visibleCount = Math.Min(MaxVisibleCommands, matchingCommands.Count);
        int windowStart = Math.Clamp(highlightedIndex - visibleCount + 1, 0, matchingCommands.Count - visibleCount);

        // Names line up in one column across the rows on screen, never wider than the row without the caret.
        int labelWidth = Math.Min(
            Math.Max(1, width - 2),
            matchingCommands.Skip(windowStart).Take(visibleCount).Max(command => CommandLabel(command).GetCellWidth()));

        // Only the window is drawn and the highlighted row wears the match colour.
        string typedCommandName = CommandLineParser.ParseName(input);
        List<Markup> rows = new(visibleCount + 1);
        for (int index = windowStart; index < windowStart + visibleCount; index++)
            rows.Add(RenderCommandRow(matchingCommands[index], typedCommandName, index == highlightedIndex, labelWidth, width));

        // Past the window the position tells the user how much of the list is off screen.
        string controls = matchingCommands.Count > visibleCount ? $"{ControlsHint} · {highlightedIndex + 1}/{matchingCommands.Count}" : ControlsHint;
        rows.Add(new Markup($"[{HintStyle}]{Markup.Escape(cells.Trunc(controls, width))}[/]"));

        // The commands on screen and the controls line.
        return rows;
    }

    /// <summary>The available commands matching the name typed so far, best match first.</summary>
    private IReadOnlyList<CommandDescriptor> FindMatchingCommands(string input, TerminalConversationState state)
    {
        // Empty right after the slash: an empty name matches every command, so a bare slash lists them all.
        string typedCommandName = CommandLineParser.ParseName(input);

        // Commands of equal rank keep the registry's order, so the list does not reshuffle between keystrokes. The registry
        // already left out what a spent conversation forbids.
        return
        [
            .. registry.ListAvailableCommands(state)
                .Select((command, order) => (Command: command, Order: order, Rank: RankCommandAgainstTypedName(command, typedCommandName)))
                .Where(candidate => candidate.Rank >= 0)
                .OrderBy(candidate => candidate.Rank)
                .ThenBy(candidate => candidate.Order)
                .Select(candidate => candidate.Command)
        ];
    }

    /// <summary>How well <paramref name="command"/> matches <paramref name="typedCommandName"/>, lower being better; -1 when it does not.</summary>
    private static int RankCommandAgainstTypedName(CommandDescriptor command, string typedCommandName)
    {
        // Best: what was typed begins the name, as /e begins /exit.
        if (command.Name.StartsWith(typedCommandName, StringComparison.OrdinalIgnoreCase))
            return 0;

        // Then the name merely contains it.
        if (command.Name.Contains(typedCommandName, StringComparison.OrdinalIgnoreCase))
            return 1;

        // Then the description does, so a user who remembers what a command does but not what it is called still finds it.
        return command.Description.Contains(typedCommandName, StringComparison.OrdinalIgnoreCase) ? 2 : -1;
    }

    /// <summary>One command as a row: the caret when highlighted, the name padded to <paramref name="labelWidth"/>, then as much of the description as the row holds.</summary>
    // The whole row wears the match colour when it is highlighted.
    private Markup RenderCommandRow(CommandDescriptor command, string typedCommandName, bool isHighlighted, int labelWidth, int width)
    {
        // The caret column is two cells wide on every row, highlighted or not, so the names stay aligned.
        StringBuilder row = new(width + 64);
        row.Append(isHighlighted ? $"[{MatchColor}]❯[/] " : "  ");

        // The highlighted row wears the match colour from name to description, while the other names stay plain white.
        string label = cells.Trunc(CommandLabel(command), labelWidth);
        AppendWithTypedFragmentInBold(row, label, typedCommandName, isHighlighted ? MatchColor : CandidateNameColor);
        row.Append(' ', Math.Max(0, labelWidth - label.GetCellWidth()));

        // What is left after caret, name and a two-cell gap; a terminal this narrow shows names alone.
        int remainingCells = width - 2 - labelWidth - 2;
        if (remainingCells > 0)
        {
            // The description arrives from the network, so it is cleaned before it is drawn.
            row.Append("  ");
            string description = cells.Trunc(cells.SanitizeForTerminal(command.Description), remainingCells);
            AppendWithTypedFragmentInBold(row, description, typedCommandName, isHighlighted ? MatchColor : DescriptionStyle);
        }

        // The row as one markup of at most the width.
        return new Markup(row.ToString());
    }

    /// <summary>Appends <paramref name="text"/> in <paramref name="style"/>, with the first occurrence of <paramref name="typedFragment"/> in bold.</summary>
    private static void AppendWithTypedFragmentInBold(StringBuilder row, string text, string typedFragment, string style)
    {
        // The bold letters show why a command is listed: where what was typed occurs in its name or description.
        int fragmentStart = typedFragment.Length == 0 ? -1 : text.IndexOf(typedFragment, StringComparison.OrdinalIgnoreCase);
        if (fragmentStart < 0)
        {
            // A fragment that does not occur leaves the whole text in the plain style.
            AppendStyled(row, text, style);
            return;
        }

        // The text around the match keeps the plain style and the match itself is bold.
        AppendStyled(row, text[..fragmentStart], style);
        AppendStyled(row, text.Substring(fragmentStart, typedFragment.Length), $"bold {style}");
        AppendStyled(row, text[(fragmentStart + typedFragment.Length)..], style);
    }

    /// <summary>Appends <paramref name="text"/> escaped and wrapped in <paramref name="style"/>, nothing for an empty piece.</summary>
    private static void AppendStyled(StringBuilder row, string text, string style)
    {
        // An empty piece would leave an empty pair of tags in the row.
        if (text.Length > 0)
            row.Append('[').Append(style).Append(']').Append(Markup.Escape(text)).Append("[/]");
    }

    /// <summary>The name as the user types it, then the options it takes.</summary>
    private string CommandLabel(CommandDescriptor command)
    {
        // Optional values are bracketed and required ones bare, so the line doubles as the spelling to copy. An option taking
        // a few values names them, since the user could otherwise only guess.
        string options = command.Options is { Count: > 0 } declaredOptions
            ? " " + string.Join(' ', declaredOptions.Select(option => option.Required ? OptionSpelling(option) : $"[{OptionSpelling(option)}]"))
            : string.Empty;

        // The label arrives from the network, so it is cleaned of anything the terminal would obey.
        return cells.SanitizeForTerminal($"/{command.Name}{options}");
    }

    /// <summary>The option as it is written at the prompt: <c>format:text|json</c> when its values are declared, <c>path:&lt;value&gt;</c> otherwise.</summary>
    private static string OptionSpelling(CommandOption option) =>
        option.AllowedValues is { Count: > 0 } allowedValues ? $"{option.Name}:{string.Join('|', allowedValues)}" : $"{option.Name}:<value>";

    /// <summary>Where the highlight sits for <paramref name="input"/> among <paramref name="matchCount"/> matching commands.</summary>
    private int HighlightedIndexFor(string input, int matchCount)
    {
        // A different line means a different list, so the highlight goes back to the best match; an unchanged line keeps it
        // where the arrows left it.
        if (!string.Equals(input, highlightedForInput, StringComparison.Ordinal))
        {
            // The highlight belongs to the line it was placed against.
            highlightedForInput = input;
            highlightedCommandIndex = 0;
        }

        // Morgana's catalogue joining mid-line can change the list under a highlight that was valid a moment ago.
        highlightedCommandIndex = Math.Clamp(highlightedCommandIndex, 0, Math.Max(0, matchCount - 1));
        return highlightedCommandIndex;
    }
}