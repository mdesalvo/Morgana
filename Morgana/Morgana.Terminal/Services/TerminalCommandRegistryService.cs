using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Morgana.Contracts;
using Morgana.Terminal.Abstractions;
using Morgana.Terminal.Interfaces;

namespace Morgana.Terminal.Services;

/// <summary>
/// Every command a TTY channel's palette offers; the one place they run from. The terminal's own commands
/// are discovered at startup: every public concrete <see cref="TerminalCommand"/> in this library and in the
/// channel, built from DI. Morgana's join as descriptors once the catalogue has been read; they run on Morgana.
/// </summary>
public sealed class TerminalCommandRegistryService
{
    /// <summary>Reads Morgana's catalogue and forwards its commands.</summary>
    private readonly MorganaClientService morganaClientService;

    /// <summary>The conversation on screen, which Morgana's commands run on.</summary>
    private readonly TerminalSessionService session;

    /// <summary>The commands the terminal runs itself, in discovery order; the palette sorts what it lists.</summary>
    private readonly IReadOnlyList<TerminalCommand> terminalCommands;

    /// <summary>The commands Morgana runs, as its catalogue described them; empty until the catalogue answers, then replaced whole.</summary>
    private volatile IReadOnlyList<CommandDescriptor> morganaCommands = [];

    /// <summary>Discovers and builds the terminal's own commands.</summary>
    /// <exception cref="InvalidOperationException">Thrown when two of them answer to the same name or alias.</exception>
    public TerminalCommandRegistryService(IServiceProvider services, MorganaClientService morganaClientService, TerminalSessionService session)
    {
        this.morganaClientService = morganaClientService;
        this.session = session;

        // The library brings /new and /exit; the channel's own assembly may add commands of its own
        Assembly[] commandAssemblies = [.. new[] { typeof(TerminalCommand).Assembly, Assembly.GetEntryAssembly() }.OfType<Assembly>().Distinct()];

        // Only what a user can pick is discovered: public concrete commands, not the abstract base
        List<TerminalCommand> discoveredCommands = [];
        foreach (Type commandType in commandAssemblies
                     .SelectMany(assembly => assembly.GetExportedTypes())
                     .Where(type => type is { IsClass: true, IsAbstract: false } && type.IsSubclassOf(typeof(TerminalCommand))))
        {
            // Each command asks DI for what it needs, as a service would
            TerminalCommand command = (TerminalCommand)ActivatorUtilities.CreateInstance(services, commandType);

            // Two commands answering to one name would leave the palette guessing which one Enter runs
            if (NameAndAliasesOf(command.Descriptor).FirstOrDefault(name => discoveredCommands.Any(other => AnswersToName(other.Descriptor, name))) is { } clashingName)
                throw new InvalidOperationException($"Command name '{clashingName}' is claimed by two terminal commands.");
            discoveredCommands.Add(command);
        }

        terminalCommands = discoveredCommands;
    }

    /// <summary>The commands the palette may list now, the terminal's and Morgana's together, in alphabetical order.</summary>
    public IReadOnlyList<CommandDescriptor> ListAvailableCommands(bool conversationSpent) =>
    [
        // A spent conversation keeps only the terminal commands allowed there, since Morgana refuses to work on it.
        // One alphabetical list, as Claude Code shows it: where a command runs is not the user's concern
        .. terminalCommands.Where(command => !conversationSpent || command.AvailableWhenSpent).Select(command => command.Descriptor)
            .Concat(conversationSpent ? [] : morganaCommands)
            .OrderBy(command => command.Name, StringComparer.OrdinalIgnoreCase)
    ];

    /// <summary>Runs the command named <paramref name="name"/>, here or on Morgana depending on whose it is. A command declaring it must be confirmed runs on nothing less than a <paramref name="confirmed"/> the channel obtained from the user.</summary>
    /// <exception cref="InvalidOperationException">Thrown when no command answers to the name, or when one that must be confirmed was not.</exception>
    public Task ExecuteCommandAsync(string name, ITerminalUi ui, bool confirmed, CancellationToken cancellationToken)
    {
        // A terminal command runs in this process, the one owning the screen and the conversation's lifecycle
        if (terminalCommands.FirstOrDefault(command => AnswersToName(command.Descriptor, name)) is { } terminalCommand)
        {
            // Nothing on this side of the wire would refuse an unconfirmed run, so the same gate Morgana
            // applies to its own commands is applied here to the terminal's
            RefuseUnconfirmed(terminalCommand.Descriptor, confirmed);
            return terminalCommand.ExecuteAsync(ui, cancellationToken);
        }

        // Any other name must be one of Morgana's: the palette never offers a name neither side knows
        if (morganaCommands.FirstOrDefault(descriptor => AnswersToName(descriptor, name)) is not { } morganaCommand)
            throw new InvalidOperationException($"No command answers to '{name}'.");

        // Morgana's command is a turn: echoed as the user's line, answered as a reply. The conversation is read
        // now, not when the catalogue came, since /new may have replaced it in between
        RefuseUnconfirmed(morganaCommand, confirmed);
        return ui.SubmitTurnAsync(
            $"/{morganaCommand.Name}",
            () => morganaClientService.RunCommandAsync(session.ConversationId, morganaCommand.Name, confirmed, cancellationToken));
    }

    /// <summary>Reads the commands Morgana publishes, keeping those the palette can offer.</summary>
    public async Task LoadMorganaCatalogAsync(CancellationToken cancellationToken)
    {
        // Read once per process: a Morgana that is down or lacks the endpoint lands in the catch below
        IReadOnlyList<CommandDescriptor> catalog;
        try
        {
            catalog = await morganaClientService.GetCommandCatalogAsync(cancellationToken);
        }
        catch (Exception)
        {
            // The palette keeps the terminal's commands alone: the conversation must not fail over a catalogue,
            // and logging is silenced under the live UI
            return;
        }

        morganaCommands =
        [
            .. catalog
                // A name the user cannot type as one word could never be resolved from the prompt
                .Where(descriptor => !string.IsNullOrWhiteSpace(descriptor.Name) && !descriptor.Name.Any(char.IsWhiteSpace))
                // The terminal's own command keeps the name: typing it must do what the user knows it does
                .Where(descriptor => !NameAndAliasesOf(descriptor).Any(name => terminalCommands.Any(command => AnswersToName(command.Descriptor, name))))
        ];
    }

    /// <summary>Refuses <paramref name="command"/> when it asks the user to confirm and <paramref name="confirmed"/> says nobody did.</summary>
    /// <exception cref="InvalidOperationException">Thrown when the command must be confirmed and was not.</exception>
    private static void RefuseUnconfirmed(CommandDescriptor command, bool confirmed)
    {
        // The user sees this as the command failing, which is what an unanswered question must amount to
        if (command.RequiresConfirmation && !confirmed)
            throw new InvalidOperationException($"'{command.Name}' runs only on an explicit confirmation.");
    }

    /// <summary>Tells whether <paramref name="name"/>, without slash, is the command's name or one of its aliases, ignoring case.</summary>
    private static bool AnswersToName(CommandDescriptor descriptor, string name) =>
        NameAndAliasesOf(descriptor).Any(candidate => string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The name and every alias a command answers to.</summary>
    private static IEnumerable<string> NameAndAliasesOf(CommandDescriptor descriptor) => [descriptor.Name, .. descriptor.Aliases ?? []];
}