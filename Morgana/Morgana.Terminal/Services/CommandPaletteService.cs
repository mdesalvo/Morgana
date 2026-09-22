using System.Text;
using Morgana.Contracts;
using Morgana.Terminal.Messages;
using Spectre.Console;

namespace Morgana.Terminal.Services;

/// <summary>
/// The dropdown that opens under the prompt as soon as the line starts with <c>/</c>: which commands match,
/// which one is highlighted, what Tab completes to, what Enter runs and how it is drawn. Every TTY channel
/// gets the same palette in its own <see cref="CommandPaletteTheme"/>. It holds the highlight, so every call
/// must come under the render lock of the UI that owns it.
/// </summary>
public sealed class CommandPaletteService
{
    /// <summary>How many commands are listed at once; the window follows the highlight past it.</summary>
    private const int MaxVisibleCommands = 6;

    /// <summary>Controls line: a terminal gives no cue that a list can be walked, so the keys are spelled out.</summary>
    private const string ControlsHint = "↑↓ move · Tab complete · Enter run · Esc cancel";

    /// <summary>Names of the candidates that are not highlighted, leaving the channel's primary colour to the one that is.</summary>
    private const string CandidateNameColor = "white";

    /// <summary>Descriptions in the de-emphasised grey the other TTY surfaces use.</summary>
    private const string DescriptionStyle = "grey70";

    /// <summary>Style of the controls line.</summary>
    private const string HintStyle = "grey54 italic";

    /// <summary>Supplies the commands to match.</summary>
    private readonly TerminalCommandRegistryService registry;

    /// <summary>Measures and cuts every row to the width the UI's row budget counts on.</summary>
    private readonly TerminalCellService cells;

    /// <summary>The channel's primary colour, which marks the highlighted candidate.</summary>
    private readonly CommandPaletteTheme theme;

    /// <summary>The line the highlight was placed against; any other line puts it back on the first match.</summary>
    private string highlightedForInput = string.Empty;

    /// <summary>Position of the highlighted command among the commands matching <see cref="highlightedForInput"/>.</summary>
    private int highlightedCommandIndex;

    /// <summary>Captures the registry, the cell measurement and the channel's theme.</summary>
    public CommandPaletteService(TerminalCommandRegistryService registry, TerminalCellService cells, CommandPaletteTheme theme)
    {
        this.registry = registry;
        this.cells = cells;
        this.theme = theme;
    }

    /// <summary>Tells whether <paramref name="input"/> is a command line: a slash in first position puts the prompt in command mode.</summary>
    public static bool IsCommandLine(string input) => input.StartsWith('/');

    /// <summary>Moves the highlight by <paramref name="rowOffset"/> rows, wrapping at both ends.</summary>
    public void MoveHighlight(string input, bool conversationSpent, int rowOffset)
    {
        // The highlight walks the list the user is looking at, the commands the typed name still matches
        IReadOnlyList<CommandDescriptor> matchingCommands = FindMatchingCommands(input, conversationSpent);
        if (matchingCommands.Count == 0)
            return;

        // Moving up from the first command lands on the last one and down from the last on the first
        int current = HighlightedIndexFor(input, matchingCommands.Count);
        highlightedCommandIndex = ((current + rowOffset) % matchingCommands.Count + matchingCommands.Count) % matchingCommands.Count;

    }

    /// <summary>The line Tab turns <paramref name="input"/> into: the highlighted command spelled out. Null when no command matches.</summary>
    public string? CompleteHighlightedCommand(string input, bool conversationSpent)
    {
        // Tab completes to the highlighted row, the one the user sees selected, not necessarily the first
        IReadOnlyList<CommandDescriptor> matchingCommands = FindMatchingCommands(input, conversationSpent);

        if (matchingCommands.Count == 0)
            return null;

        // Only the name is rewritten, an alias included: values already written stay as they were typed, while
        // a command that takes them leaves the caret past a space, where the first one goes
        CommandDescriptor completed = matchingCommands[HighlightedIndexFor(input, matchingCommands.Count)];
        int optionsStart = input.IndexOf(' ');
        if (optionsStart >= 0)
            return $"/{completed.Name}{input[optionsStart..]}";

        return completed.Options is { Count: > 0 } ? $"/{completed.Name} " : $"/{completed.Name}";
    }

    /// <summary>The command Enter runs for <paramref name="input"/>; null when nothing matches it.</summary>
    public CommandDescriptor? ResolveCommandToRun(string input, bool conversationSpent)
    {
        IReadOnlyList<CommandDescriptor> matchingCommands = FindMatchingCommands(input, conversationSpent);
        if (matchingCommands.Count == 0)
            return null;

        // Enter runs the highlighted row, as in Claude Code: the first match until the arrows move to another
        return matchingCommands[HighlightedIndexFor(input, matchingCommands.Count)];
    }

    /// <summary>The colour of a match: the highlighted candidate and a typed line naming a command exactly, in the channel's primary.</summary>
    public string MatchColor => theme.PrimaryColor;

    /// <summary>Tells whether <paramref name="input"/> names an available command exactly, by its name or an alias.</summary>
    public bool NamesCommandExactly(string input, bool conversationSpent)
    {
        // The line is drawn in the match colour at this point, as Claude Code does: what was typed is a command in itself
        string typedCommandName = CommandLineParser.ParseName(input);
        return IsCommandLine(input) && registry.ListAvailableCommands(conversationSpent)
            .Any(command => NameAndAliasesOf(command).Any(name => string.Equals(name, typedCommandName, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// Draws the palette for <paramref name="input"/>: the matching commands, then the controls line. Every row is one
    /// markup of at most <paramref name="width"/> cells, so the UI can budget them against its history.
    /// </summary>
    public List<Markup> RenderPalette(string input, bool conversationSpent, int width)
    {
        // A terminal reporting no width still gets one cell per row
        width = Math.Max(1, width);

        // A line without the slash has no palette; the highlight is forgotten with it: deleting the slash and
        // typing it again starts from the first command
        if (!IsCommandLine(input))
        {
            highlightedForInput = string.Empty;
            return [];
        }

        // The line typed is the user's own, but it is echoed back through Markup, so it is cleaned all the same
        IReadOnlyList<CommandDescriptor> matchingCommands = FindMatchingCommands(input, conversationSpent);
        if (matchingCommands.Count == 0)
            return [new Markup($"[{DescriptionStyle}]{Markup.Escape(cells.Trunc(SanitizeForTerminal($"no command matches {input}"), width))}[/]")];

        // The window ends on the highlight once it moves past the last visible row, so it is always on screen
        int highlighted = HighlightedIndexFor(input, matchingCommands.Count);
        int visibleCount = Math.Min(MaxVisibleCommands, matchingCommands.Count);
        int windowStart = Math.Clamp(highlighted - visibleCount + 1, 0, matchingCommands.Count - visibleCount);

        // Names line up in one column across the rows on screen, never wider than the row minus the caret
        int labelWidth = Math.Min(
            Math.Max(1, width - 2),
            matchingCommands.Skip(windowStart).Take(visibleCount).Max(command => CommandLabel(command).GetCellWidth()));

        // Only the window is drawn; the highlighted row wears the match colour, the first one until the arrows move it
        string typedCommandName = CommandLineParser.ParseName(input);
        List<Markup> rows = new(visibleCount + 1);
        for (int index = windowStart; index < windowStart + visibleCount; index++)
            rows.Add(RenderCommandRow(matchingCommands[index], typedCommandName, index == highlighted, labelWidth, width));

        // Past the window the position tells the user how much of the list is off screen
        string controls = matchingCommands.Count > visibleCount ? $"{ControlsHint} · {highlighted + 1}/{matchingCommands.Count}" : ControlsHint;
        rows.Add(new Markup($"[{HintStyle}]{Markup.Escape(cells.Trunc(controls, width))}[/]"));
        return rows;
    }

    /// <summary>The available commands matching the name typed so far, best match first.</summary>
    private IReadOnlyList<CommandDescriptor> FindMatchingCommands(string input, bool conversationSpent)
    {
        // Empty right after the slash; an empty name matches every command, so a bare slash lists them all
        string typedCommandName = CommandLineParser.ParseName(input);

        // Commands of equal rank keep the registry's order, so the list does not reshuffle between keystrokes.
        // The registry already left out what a spent conversation forbids
        return
        [
            .. registry.ListAvailableCommands(conversationSpent)
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
        // Best: what was typed begins the name or an alias, as /e begins /exit through its alias esc
        if (NameAndAliasesOf(command).Any(name => name.StartsWith(typedCommandName, StringComparison.OrdinalIgnoreCase)))
            return 0;

        // Then the name merely contains it, then the description does: the user who remembers what a command
        // does but not what it is called still finds it
        if (command.Name.Contains(typedCommandName, StringComparison.OrdinalIgnoreCase))
            return 1;
        return command.Description.Contains(typedCommandName, StringComparison.OrdinalIgnoreCase) ? 2 : -1;
    }

    /// <summary>
    /// Draws one command: the caret when highlighted, the name with its aliases padded to <paramref name="labelWidth"/>,
    /// then as much of the description as the row holds. The whole row wears the match colour when <paramref name="highlighted"/>.
    /// </summary>
    private Markup RenderCommandRow(CommandDescriptor command, string typedCommandName, bool highlighted, int labelWidth, int width)
    {
        // The caret column is two cells wide on every row, highlighted or not, so the names stay aligned
        StringBuilder row = new(width + 64);
        row.Append(highlighted ? $"[{MatchColor}]❯[/] " : "  ");

        // The highlighted row wears the match colour from name to description; the other names stay plain white
        string label = cells.Trunc(CommandLabel(command), labelWidth);
        AppendWithTypedFragmentInBold(row, label, typedCommandName, highlighted ? MatchColor : CandidateNameColor);
        row.Append(' ', Math.Max(0, labelWidth - label.GetCellWidth()));

        // What is left after caret, name and a two-cell gap; a terminal this narrow shows names alone
        int remaining = width - 2 - labelWidth - 2;
        if (remaining > 0)
        {
            row.Append("  ");
            string description = cells.Trunc(SanitizeForTerminal(command.Description), remaining);
            AppendWithTypedFragmentInBold(row, description, typedCommandName, highlighted ? MatchColor : DescriptionStyle);
        }
        return new Markup(row.ToString());
    }

    /// <summary>Appends <paramref name="text"/> in <paramref name="style"/>, with the first occurrence of <paramref name="typedFragment"/> in bold.</summary>
    private static void AppendWithTypedFragmentInBold(StringBuilder row, string text, string typedFragment, string style)
    {
        // The bold letters show why a command is listed: where what was typed occurs in its name or description
        int fragmentStart = typedFragment.Length == 0 ? -1 : text.IndexOf(typedFragment, StringComparison.OrdinalIgnoreCase);
        if (fragmentStart < 0)
        {
            AppendStyled(row, text, style);
            return;
        }

        AppendStyled(row, text[..fragmentStart], style);
        AppendStyled(row, text.Substring(fragmentStart, typedFragment.Length), $"bold {style}");
        AppendStyled(row, text[(fragmentStart + typedFragment.Length)..], style);
    }

    /// <summary>Appends <paramref name="text"/> escaped and wrapped in <paramref name="style"/>; nothing for an empty piece.</summary>
    private static void AppendStyled(StringBuilder row, string text, string style)
    {
        if (text.Length > 0)
            row.Append('[').Append(style).Append(']').Append(Markup.Escape(text)).Append("[/]");
    }

    /// <summary>The name and every alias a command answers to.</summary>
    private static IEnumerable<string> NameAndAliasesOf(CommandDescriptor command) => [command.Name, .. command.Aliases ?? []];

    /// <summary>The name as the user types it, the aliases that also reach it, then the options it takes.</summary>
    private string CommandLabel(CommandDescriptor command)
    {
        string aliases = command.Aliases is { Count: > 0 } names ? $" ({string.Join(", ", names)})" : string.Empty;

        // Optional values are bracketed, required ones bare: the line doubles as the spelling to copy
        string options = command.Options is { Count: > 0 } declared
            ? " " + string.Join(' ', declared.Select(option => option.Required ? $"{option.Name}:<value>" : $"[{option.Name}:<value>]"))
            : string.Empty;

        return SanitizeForTerminal($"/{command.Name}{aliases}{options}");
    }

    /// <summary>
    /// Prepares text published by Morgana for the terminal. It arrives from the network, so control characters
    /// are stripped before they can steer the user's terminal. Emoji are resolved and variation selectors
    /// dropped first, so the width measured is the width drawn.
    /// </summary>
    private string SanitizeForTerminal(string text) =>
        TerminalCellService.StripControlCharacters(cells.StripVariationSelectors(Emoji.Replace(text)));

    /// <summary>Where the highlight sits for <paramref name="input"/> among <paramref name="matchCount"/> matching commands.</summary>
    private int HighlightedIndexFor(string input, int matchCount)
    {
        // A different line means a different list: the highlight goes back to the best match. An unchanged line
        // keeps it where the arrows left it
        if (!string.Equals(input, highlightedForInput, StringComparison.Ordinal))
        {
            highlightedForInput = input;
            highlightedCommandIndex = 0;
        }

        // Morgana's catalogue joining mid-line can change the list under a highlight that was valid a moment ago
        highlightedCommandIndex = Math.Clamp(highlightedCommandIndex, 0, Math.Max(0, matchCount - 1));
        return highlightedCommandIndex;
    }
}
