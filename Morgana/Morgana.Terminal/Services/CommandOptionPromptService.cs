using Morgana.Contracts;
using Morgana.Terminal.Messages;
using Spectre.Console;

namespace Morgana.Terminal.Services;

/// <summary>
/// The form a command opens when it is run without the values it declares: which option is being asked,
/// what has been given so far and what the user is looking at while they type. A command describes its
/// options once, so asking for them is the channel's work rather than each command's.
/// </summary>
/// <remarks>
/// The line being typed stays the prompt's own: this holds the question, never the keystrokes, so the caret,
/// the arrows and the deletions keep working exactly as they do on a message. Every call must come under the
/// render lock of the UI that owns it.
/// </remarks>
public sealed class CommandOptionPromptService
{
    /// <summary>Controls line: what Enter does here is not what it does at a prompt, so it is spelled out.</summary>
    private const string ControlsHint = "Enter accepts · Esc cancels";

    /// <summary>Style of the option's own description, in the de-emphasised grey the other command surfaces use.</summary>
    private const string DescriptionStyle = "grey70";

    /// <summary>Style of the controls line.</summary>
    private const string HintStyle = "grey54 italic";

    /// <summary>Style of the line saying a value cannot be left out, which is a refusal rather than a hint.</summary>
    private const string RefusalStyle = "orange1";

    /// <summary>Measures and cuts every row to the width the UI's row budget counts on.</summary>
    private readonly TerminalCellService cells;

    /// <summary>The channel's colour, which names the command being filled in.</summary>
    private readonly CommandTheme theme;

    /// <summary>The command being filled in, null when the prompt is free.</summary>
    private CommandDescriptor? command;

    /// <summary>The options still to ask, in the order the command declares them.</summary>
    private readonly Queue<CommandOption> pendingOptions = new();

    /// <summary>What has been given so far, the values typed at the prompt included.</summary>
    private Dictionary<string, string> gatheredOptions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True once a required option was answered with nothing, which is said on screen until it is answered.</summary>
    private bool lastAnswerWasEmpty;

    /// <summary>Captures the cell measurement and the channel's theme.</summary>
    public CommandOptionPromptService(TerminalCellService cells, CommandTheme theme)
    {
        this.cells = cells;
        this.theme = theme;
    }

    /// <summary>Tells whether a command is being filled in, which gives the prompt's Enter a different meaning.</summary>
    public bool IsActive => command is not null;

    /// <summary>
    /// Tells whether <paramref name="candidate"/> cannot run on <paramref name="typedOptions"/> alone because
    /// something it declares as required was not written: that is what the form exists to collect.
    /// </summary>
    public static bool NeedsFillingIn(CommandDescriptor candidate, IReadOnlyDictionary<string, string> typedOptions) =>
        (candidate.Options ?? []).Any(option => option.Required
            && !typedOptions.Keys.Any(given => string.Equals(given, option.Name, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Opens the form for <paramref name="candidate"/>, keeping the <paramref name="typedOptions"/> already
    /// written and asking for the rest: an optional one is asked too, since an empty answer skips it.
    /// </summary>
    public void Begin(CommandDescriptor candidate, IReadOnlyDictionary<string, string> typedOptions)
    {
        command = candidate;
        gatheredOptions = new Dictionary<string, string>(typedOptions, StringComparer.OrdinalIgnoreCase);
        lastAnswerWasEmpty = false;

        pendingOptions.Clear();
        foreach (CommandOption option in candidate.Options ?? [])
        {
            // What was already written at the prompt is not asked again: the user would have to type it twice
            if (!gatheredOptions.ContainsKey(option.Name))
                pendingOptions.Enqueue(option);
        }
    }

    /// <summary>The command being filled in, null when the prompt is free.</summary>
    public CommandDescriptor? PendingCommand => command;

    /// <summary>The option being asked for, null when nothing is.</summary>
    public CommandOption? CurrentOption => pendingOptions.Count > 0 ? pendingOptions.Peek() : null;

    /// <summary>Closes the form, leaving the command unrun.</summary>
    public void Cancel()
    {
        command = null;
        pendingOptions.Clear();

        // The values gathered are handed to the invocation that runs them, so closing the form starts a fresh
        // collection rather than emptying the one already given away
        gatheredOptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Takes <paramref name="value"/> as the answer to the option being asked and moves to the next one.
    /// An empty answer skips an optional value; on a required one it is refused, which leaves the same
    /// question standing. The invocation comes back once nothing is left to ask, and the form closes with it.
    /// </summary>
    public CommandInvocation? Accept(string value)
    {
        if (command is not { } filledCommand || CurrentOption is not { } option)
            return null;

        string answer = value.Trim();

        // A command cannot be run without what it declares it needs, so the question stays where it is
        lastAnswerWasEmpty = answer.Length == 0 && option.Required;
        if (lastAnswerWasEmpty)
            return null;

        // An empty answer to an optional value means the user does not want it: the command reads its own default
        if (answer.Length > 0)
            gatheredOptions[option.Name] = answer;
        pendingOptions.Dequeue();

        if (pendingOptions.Count > 0)
            return null;

        CommandInvocation invocation = new(filledCommand, gatheredOptions);
        Cancel();
        return invocation;
    }

    /// <summary>
    /// Draws the form above the line being typed: which command is being filled in, the option being asked
    /// with its own description, then the controls. Every row is one markup of at most
    /// <paramref name="width"/> cells, so the UI budgets them as it budgets its other prompt rows.
    /// </summary>
    public List<Markup> RenderPrompt(int width)
    {
        if (command is not { } filledCommand || CurrentOption is not { } option)
            return [];

        // A terminal reporting no width still gets one cell per row
        width = Math.Max(1, width);

        // The command is named on every row of the form, since the line that opened it is gone from the prompt
        string title = $"/{filledCommand.Name} · {option.Name}{(option.Required ? string.Empty : " (optional)")}";

        List<Markup> rows =
        [
            new Markup($"[{theme.PrimaryColor}]{Markup.Escape(cells.Trunc(SanitizeForTerminal(title), width))}[/]"),
            new Markup($"[{DescriptionStyle}]{Markup.Escape(cells.Trunc(SanitizeForTerminal($"  {option.Description}"), width))}[/]")
        ];

        // What was refused is said where it happened, so an unanswered Enter does not read as a dead key
        if (lastAnswerWasEmpty)
            rows.Add(new Markup($"[{RefusalStyle}]{Markup.Escape(cells.Trunc($"  {option.Name} cannot be left out", width))}[/]"));

        // An optional value is skipped by answering nothing, which nothing on screen would otherwise suggest
        string controls = option.Required ? ControlsHint : $"{ControlsHint} · empty Enter skips it";
        rows.Add(new Markup($"[{HintStyle}]{Markup.Escape(cells.Trunc(controls, width))}[/]"));
        return rows;
    }

    /// <summary>
    /// Prepares text published by Morgana for the terminal. A command's own description arrives from the
    /// network, so control characters are stripped before they can steer the user's terminal. Emoji are
    /// resolved and variation selectors dropped first, so the width measured is the width drawn.
    /// </summary>
    private string SanitizeForTerminal(string text) =>
        TerminalCellService.StripControlCharacters(cells.StripVariationSelectors(Emoji.Replace(text)));
}
