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
public record CommandDescriptor(
    string Name,
    string Description,
    IReadOnlyList<string>? Aliases = null,
    bool RequiresConfirmation = false);