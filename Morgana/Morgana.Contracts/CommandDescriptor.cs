namespace Morgana.Contracts;

/// <summary>
/// A command a channel can offer its user, described the way a command palette lists it. Morgana
/// publishes its own through <c>GET /api/morgana/commands</c>; a channel describes its local ones
/// with the same shape, so its palette lists both without telling them apart.
/// </summary>
/// <param name="Name">The command without its leading slash, lowercase: <c>new</c>, not <c>/New</c>.</param>
/// <param name="Description">One line telling the user what running it does.</param>
/// <param name="Aliases">Further names that resolve to the same command, lowercase and without slash.</param>
/// <param name="RequiresConfirmation">
/// True when the command does something the user cannot take back, so a channel must obtain an explicit
/// Yes/No answer before running it and Morgana refuses a run that does not carry one.
/// </param>
/// <param name="Options">The values the command accepts at the prompt; a command taking none declares nothing.</param>
/// <param name="RequiresActiveAgent">
/// True when the command acts on the desk carrying the conversation, so it is offered only while one is
/// carrying it and refused otherwise. What Morgana says in her own voice — a welcome, a refusal, a
/// disambiguation — belongs to no desk, so a command scoped this way has nothing to act on there.
/// </param>
public record CommandDescriptor(
    string Name,
    string Description,
    IReadOnlyList<string>? Aliases = null,
    bool RequiresConfirmation = false,
    IReadOnlyList<CommandOption>? Options = null,
    bool RequiresActiveAgent = false)
{
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

        // What the command cannot work without is asked for before anything happens, not discovered halfway through
        if (declared.FirstOrDefault(option => option.Required && (options is null
                || !options.Keys.Any(given => string.Equals(given, option.Name, StringComparison.OrdinalIgnoreCase)))) is { } missing)
            return $"/{Name} needs {missing.Name}: {missing.Description}";

        return null;
    }
}
