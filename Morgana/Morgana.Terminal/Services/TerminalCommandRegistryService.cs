using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Morgana.Contracts;
using Morgana.Terminal.Abstractions;
using Morgana.Terminal.Interfaces;
using Morgana.Terminal.Messages;

namespace Morgana.Terminal.Services;

/// <summary>Every command that a TTY channel's palette offers and the one place they run from.</summary>
// The terminal's own commands are discovered at startup: every public concrete TerminalCommand in this library and in the
// channel, built from DI. Morgana's join as descriptors once its catalogue has been read and they run on Morgana.
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
    /// <exception cref="InvalidOperationException">Two of them answer to the same name or one declares itself wrongly.</exception>
    public TerminalCommandRegistryService(IServiceProvider services, MorganaClientService morganaClientService, TerminalSessionService session)
    {
        // The client reaches Morgana's commands and the session names the conversation they run on.
        this.morganaClientService = morganaClientService;
        this.session = session;

        // The library brings the commands every TTY channel shares; the channel's own assembly may add commands of its own.
        Assembly[] commandAssemblies = [.. new[] { typeof(TerminalCommand).Assembly, Assembly.GetEntryAssembly() }.OfType<Assembly>().Distinct()];

        // Only what a user can pick is discovered: public concrete commands, not the abstract base.
        List<TerminalCommand> discoveredCommands = [];
        foreach (Type commandType in commandAssemblies
                     .SelectMany(assembly => assembly.GetExportedTypes())
                     .Where(type => type is { IsClass: true, IsAbstract: false } && type.IsSubclassOf(typeof(TerminalCommand))))
        {
            // Each command asks DI for what it needs, as a service would.
            TerminalCommand command = (TerminalCommand)ActivatorUtilities.CreateInstance(services, commandType);

            // A declaration the prompt could not run as written is refused by the rule Morgana applies to its own.
            if (command.Descriptor.DescribeDeclarationProblem() is { } declarationProblem)
                throw new InvalidOperationException($"{commandType.Name}: {declarationProblem}.");

            // Two commands answering to one name would leave the palette guessing which one Enter runs.
            if (discoveredCommands.Any(otherCommand => AnswersToName(otherCommand.Descriptor, command.Descriptor.Name)))
                throw new InvalidOperationException($"Command name '{command.Descriptor.Name}' is claimed by two terminal commands.");

            // The command is one the palette can offer.
            discoveredCommands.Add(command);
        }

        // The registry lists exactly the commands that were discovered.
        terminalCommands = discoveredCommands;
    }

    /// <summary>The commands the palette may list now, the terminal's and Morgana's together, in alphabetical order.</summary>
    public IReadOnlyList<CommandDescriptor> ListAvailableCommands(TerminalConversationState state) =>
    [
        // A spent conversation keeps only the terminal commands allowed there, since Morgana refuses to work on it. One
        // alphabetical list, because where a command runs is not the user's concern.
        .. terminalCommands.Where(command => !state.Spent || command.AvailableWhenSpent).Select(command => command.Descriptor)
            .Concat(state.Spent ? [] : morganaCommands)

            // A command acting on the agent carrying the conversation is not offered while Morgana holds it herself: there
            // would be nothing for it to act on, which the user should not have to discover by running it.
            .Where(command => !command.RequiresActiveAgent || state.AgentCarriesConversation)
            .OrderBy(command => command.Name, StringComparer.OrdinalIgnoreCase)
    ];

    /// <summary>
    /// Runs <paramref name="invocation"/> here or on Morgana, depending on whose command it is, returning once it is over.
    /// A command declaring it must be confirmed runs on nothing less than a confirmed that the channel obtained from the user.
    /// </summary>
    /// <exception cref="InvalidOperationException">No command answers to the name, its options are not what it declares or one that must be confirmed was not.</exception>
    public Task ExecuteCommandAsync(CommandInvocation invocation, ITerminalUi ui, bool confirmed, CancellationToken cancellationToken)
    {
        // The name the user asked for, matched ignoring case.
        string name = invocation.Command.Name;

        // What the user left out is filled from the command's own declaration before anything runs, so a command reads one
        // set of values whether they were typed, gathered by the form or never mentioned.
        IReadOnlyDictionary<string, string> options = invocation.Command.ApplyDefaults(invocation.Options);

        // A terminal command runs in this process, the one owning the screen and the conversation's lifecycle.
        if (terminalCommands.FirstOrDefault(command => AnswersToName(command.Descriptor, name)) is { } terminalCommand)
        {
            // Nothing on this side of the wire would refuse a malformed or unconfirmed run, so the gates Morgana applies to
            // its own commands are applied here to the terminal's.
            RefuseUnrunnable(terminalCommand.Descriptor, options, confirmed);
            return terminalCommand.ExecuteAsync(ui, options, cancellationToken);
        }

        // Any other name must be one of Morgana's, since the palette never offers a name neither side knows.
        if (morganaCommands.FirstOrDefault(descriptor => AnswersToName(descriptor, name)) is not { } morganaCommand)
            throw new InvalidOperationException($"No command answers to '{name}'.");

        // Morgana's command is no turn: nothing is echoed and no reply is awaited, since the call itself lasts as long as the
        // command. The conversation is read now, not when the catalogue came, since /new may have replaced it in between.
        RefuseUnrunnable(morganaCommand, options, confirmed);
        return morganaClientService.RunCommandAsync(session.ConversationId, morganaCommand.Name, invocation.Id, options, confirmed, cancellationToken);
    }

    /// <summary>Reads the commands Morgana publishes, keeping those the palette can offer.</summary>
    public async Task LoadMorganaCatalogAsync(CancellationToken cancellationToken)
    {
        // Read once per process: a Morgana that is down or lacks the endpoint lands in the catch below.
        IReadOnlyList<CommandDescriptor> catalog;
        try
        {
            // The catalogue is requested once Morgana is up.
            catalog = await morganaClientService.GetCommandCatalogAsync(cancellationToken);
        }
        catch (Exception)
        {
            // The palette keeps the terminal's commands alone, since the conversation must not fail over a catalogue.
            // Nothing is logged, because logging is silenced under the live UI.
            return;
        }

        // Morgana's commands are the catalogue without what cannot be typed or is already the terminal's.
        morganaCommands =
        [
            .. catalog
                // A name the user cannot type as one word could never be resolved from the prompt.
                .Where(descriptor => !string.IsNullOrWhiteSpace(descriptor.Name) && !descriptor.Name.Any(char.IsWhiteSpace))
                // The terminal's own command keeps the name, because typing it must do what the user knows it does.
                .Where(descriptor => !terminalCommands.Any(command => AnswersToName(command.Descriptor, descriptor.Name)))
        ];
    }

    /// <summary>
    /// Refuses <paramref name="command"/> when its options are not what it declares or it asks for a confirmation that <paramref name="confirmed"/> says nobody gave.
    /// The user sees these as the command failing, which is what an unanswered question or a value the command cannot use must amount to.
    /// </summary>
    /// <exception cref="InvalidOperationException">The command cannot be run as asked.</exception>
    private static void RefuseUnrunnable(CommandDescriptor command, IReadOnlyDictionary<string, string> options, bool confirmed)
    {
        // A value the command cannot use is refused with the command's own words for what it takes.
        if (command.DescribeOptionProblem(options) is { } optionProblem)
            throw new InvalidOperationException(optionProblem);

        // A command that must be confirmed never runs on a question nobody answered.
        if (command.RequiresConfirmation && !confirmed)
            throw new InvalidOperationException($"'{command.Name}' runs only on an explicit confirmation.");
    }

    /// <summary>Tells whether <paramref name="name"/>, without slash, is the command's name ignoring case.</summary>
    private static bool AnswersToName(CommandDescriptor descriptor, string name) =>
        string.Equals(descriptor.Name, name, StringComparison.OrdinalIgnoreCase);
}