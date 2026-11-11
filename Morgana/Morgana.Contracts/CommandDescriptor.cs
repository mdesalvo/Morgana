namespace Morgana.Contracts;

/// <summary>
/// A command a channel can offer its user, described the way a command palette lists it.
/// Morgana publishes its own commands through GET /api/morgana/commands and a channel describes its local ones with the same shape,
/// so its palette lists both without telling them apart. A command that RequiresConfirmation obliges the channel to obtain an
/// explicit Yes/No answer before running it and Morgana refuses a run that does not carry one. A command that RequiresActiveAgent
/// is offered only while an agent carries the conversation and refused otherwise, because what Morgana says in her own voice
/// (a welcome, a refusal, a disambiguation) belongs to no agent and leaves such a command nothing to act on.
/// </summary>
/// <param name="Name">The command without its leading slash, lowercase: <c>new</c>, not <c>/New</c>.</param>
/// <param name="Description">One line telling the user what the command does.</param>
/// <param name="RequiresConfirmation">True when the command does something the user cannot take back.</param>
/// <param name="Options">The values the command accepts at the prompt; null or empty when it takes none.</param>
/// <param name="RequiresActiveAgent">True when the command acts on the agent carrying the conversation.</param>
public record CommandDescriptor(
    string Name,
    string Description,
    bool RequiresConfirmation = false,
    IReadOnlyList<CommandOption>? Options = null,
    bool RequiresActiveAgent = false)
{
    /// <summary>What is wrong with how this command declares itself, as a line naming the fault; null when nothing is.</summary>
    // Every registry asks this at startup, so a declaration is judged by one rule wherever the command lives.
    public string? DescribeDeclarationProblem()
    {
        // A command declaring no options is read as one with an empty list, so the checks below need no null case.
        IReadOnlyList<CommandOption> declaredOptions = Options ?? [];

        // Options are matched ignoring case, so two spelled alike would hand the typed value to whichever came first
        if (declaredOptions.GroupBy(option => option.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1) is { } duplicatedOptionGroup)
            return $"/{Name} declares the option '{duplicatedOptionGroup.Key}' more than once";

        // An empty list would refuse every value, leaving an option nobody can give
        if (declaredOptions.FirstOrDefault(option => option.AllowedValues is { Count: 0 }) is { } optionWithoutValues)
            return $"/{Name} declares no value the option '{optionWithoutValues.Name}' takes";

        // A default is what the command runs on unasked, so one it would refuse makes every run without it fail
        if (declaredOptions.FirstOrDefault(option => option.DefaultValue is { } defaultValue && !option.Allows(defaultValue)) is { } optionRefusingItsDefault)
            return $"/{Name} defaults the option '{optionRefusingItsDefault.Name}' to '{optionRefusingItsDefault.DefaultValue}', which it does not take";

        // Nothing is wrong, which every registry reads as a declaration it can publish.
        return null;
    }

    /// <summary>
    /// What is wrong with <paramref name="options"/> for this command, as a line the user can read; null when nothing is.
    /// Both sides of the wire ask this, so an option is judged by one rule wherever it was typed.
    /// </summary>
    public string? DescribeOptionProblem(IReadOnlyDictionary<string, string>? options)
    {
        // A command declaring no options is read as one with an empty list, so the checks below need no null case.
        IReadOnlyList<CommandOption> declaredOptions = Options ?? [];

        // An option nobody declared is a typo or a memory of another command, never something to run on
        if (options?.Keys.FirstOrDefault(
                givenName => !declaredOptions.Any(option => string.Equals(option.Name, givenName, StringComparison.OrdinalIgnoreCase))) is { } unknownOptionName)
            return $"/{Name} takes no option named '{unknownOptionName}'";

        // A value the command cannot use is the user's to correct, so it is refused with the values that would do.
        foreach ((string givenName, string givenValue) in options ?? new Dictionary<string, string>())
        {
            // Every name given is a declared one by now, so each finds its option.
            CommandOption option = declaredOptions.First(candidate => string.Equals(candidate.Name, givenName, StringComparison.OrdinalIgnoreCase));

            // A value outside the option's AllowedValues is refused naming the values that would do.
            if (!option.Allows(givenValue))
                return $"/{Name} takes {option.Name} as {option.DescribeAllowedValues()}, not '{givenValue}'";
        }

        // What the command cannot work without is asked for before anything happens, not discovered halfway
        // through. An option carrying a default is never missing: the default is what the command runs on
        if (declaredOptions.FirstOrDefault(option => option is { Required: true, DefaultValue: null } && !WasGiven(option, options)) is { } missingOption)
            return $"/{Name} needs {missingOption.Name}: {missingOption.Description}";

        // Nothing is wrong, so the run may go ahead on these options.
        return null;
    }

    /// <summary>
    /// <paramref name="options"/> as the command will read them: what the user gave plus the declared default of every option left out.
    /// Applied once before running, so no command has to remember its own defaults and every side of the wire runs on the same values.
    /// </summary>
    public IReadOnlyDictionary<string, string> ApplyDefaults(IReadOnlyDictionary<string, string>? options)
    {
        // A copy matched ignoring case, so the caller's dictionary stays untouched and a default is never added under a second spelling of a given name.
        Dictionary<string, string> effectiveOptions = options is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(options, StringComparer.OrdinalIgnoreCase);

        // Each declared option gets its chance to contribute a default.
        foreach (CommandOption option in Options ?? [])
        {
            // Only an option declaring a default contributes it while TryAdd keeps whatever the user gave.
            if (option.DefaultValue is { } defaultValue)
                effectiveOptions.TryAdd(option.Name, defaultValue);
        }

        // The merged view is what the command runs on.
        return effectiveOptions;
    }

    /// <summary>Tells whether <paramref name="options"/> carries a value for <paramref name="option"/>, however it was spelled.</summary>
    private static bool WasGiven(CommandOption option, IReadOnlyDictionary<string, string>? options) =>
        options is not null && options.Keys.Any(givenName => string.Equals(givenName, option.Name, StringComparison.OrdinalIgnoreCase));
}