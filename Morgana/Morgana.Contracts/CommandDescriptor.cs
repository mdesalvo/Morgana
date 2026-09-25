namespace Morgana.Contracts;

/// <summary>
/// A command a channel can offer its user, described the way a command palette lists it. Morgana
/// publishes its own through <c>GET /api/morgana/commands</c>; a channel describes its local ones
/// with the same shape, so its palette lists both without telling them apart.
/// </summary>
/// <param name="Name">The command without its leading slash, lowercase: <c>new</c>, not <c>/New</c>. The one name it answers to.</param>
/// <param name="Description">One line telling the user what running it does.</param>
/// <param name="RequiresConfirmation">
/// True when the command does something the user cannot take back, so a channel must obtain an explicit
/// Yes/No answer before running it and Morgana refuses a run that does not carry one.
/// </param>
/// <param name="Options">The values the command accepts at the prompt; a command taking none declares nothing.</param>
/// <param name="RequiresActiveAgent">
/// True when the command acts on the agent carrying the conversation, so it is offered only while one is
/// carrying it and refused otherwise. What Morgana says in her own voice — a welcome, a refusal, a
/// disambiguation — belongs to no agent, so a command scoped this way has nothing to act on there.
/// </param>
public record CommandDescriptor(
    string Name,
    string Description,
    bool RequiresConfirmation = false,
    IReadOnlyList<CommandOption>? Options = null,
    bool RequiresActiveAgent = false)
{
    /// <summary>
    /// What is wrong with how this command declares itself, as a line naming the fault; null when nothing is.
    /// Every registry asks this at startup, so a declaration is judged by one rule wherever the command lives.
    /// </summary>
    public string? DescribeDeclarationProblem()
    {
        IReadOnlyList<CommandOption> declared = Options ?? [];

        // Options are matched ignoring case, so two spelled alike would hand the typed value to whichever came first
        if (declared.GroupBy(option => option.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1) is { } clash)
            return $"/{Name} declares the option '{clash.Key}' more than once";

        // An empty list would refuse every value, leaving an option nobody can give
        if (declared.FirstOrDefault(option => option.AllowedValues is { Count: 0 }) is { } closed)
            return $"/{Name} declares no value the option '{closed.Name}' takes";

        // A default is what the command runs on unasked, so one it would refuse makes every run without it fail
        if (declared.FirstOrDefault(option => option.DefaultValue is { } fallback && !option.Allows(fallback)) is { } misfit)
            return $"/{Name} defaults the option '{misfit.Name}' to '{misfit.DefaultValue}', which it does not take";

        return null;
    }

    /// <summary>
    /// What is wrong with <paramref name="options"/> for this command, as a line the user can read; null when
    /// nothing is. Both sides of the wire ask this, so an option is judged by one rule wherever it was typed.
    /// </summary>
    public string? DescribeOptionProblem(IReadOnlyDictionary<string, string>? options)
    {
        IReadOnlyList<CommandOption> declared = Options ?? [];

        // An option nobody declared is a typo or a memory of another command, never something to run on
        if (options is not null && options.Keys.FirstOrDefault(
                given => !declared.Any(option => string.Equals(option.Name, given, StringComparison.OrdinalIgnoreCase))) is { } unknown)
            return $"/{Name} takes no option named '{unknown}'";

        // A value the command cannot use is the user's to correct, so it is refused with the values that would do.
        // Every name given is a declared one by now, so each finds its option
        foreach ((string given, string value) in options ?? new Dictionary<string, string>())
        {
            CommandOption option = declared.First(candidate => string.Equals(candidate.Name, given, StringComparison.OrdinalIgnoreCase));
            if (!option.Allows(value))
                return $"/{Name} takes {option.Name} as {option.DescribeAllowedValues()}, not '{value}'";
        }

        // What the command cannot work without is asked for before anything happens, not discovered halfway
        // through. An option carrying a default is never missing: the default is what the command runs on
        if (declared.FirstOrDefault(option => option.Required && option.DefaultValue is null && !WasGiven(option, options)) is { } missing)
            return $"/{Name} needs {missing.Name}: {missing.Description}";

        return null;
    }

    /// <summary>
    /// <paramref name="options"/> as the command will read them: what the user gave, plus the declared
    /// default of every option they left out. Applied once before running, so no command has to remember
    /// its own defaults and every side of the wire runs on the same values.
    /// </summary>
    public IReadOnlyDictionary<string, string> ApplyDefaults(IReadOnlyDictionary<string, string>? options)
    {
        Dictionary<string, string> effective = options is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(options, StringComparer.OrdinalIgnoreCase);

        foreach (CommandOption option in Options ?? [])
        {
            if (option.DefaultValue is { } fallback && !effective.ContainsKey(option.Name))
                effective[option.Name] = fallback;
        }

        return effective;
    }

    /// <summary>Tells whether <paramref name="options"/> carries a value for <paramref name="option"/>, however it was spelled.</summary>
    private static bool WasGiven(CommandOption option, IReadOnlyDictionary<string, string>? options) =>
        options is not null && options.Keys.Any(given => string.Equals(given, option.Name, StringComparison.OrdinalIgnoreCase));
}
