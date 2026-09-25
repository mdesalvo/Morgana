using Morgana.AI.Interfaces;
using Morgana.Contracts;

namespace Morgana.AI.Services;

/// <summary>
/// Publishes every <see cref="ICommand"/> registered in DI, in registration order.
/// Two commands claiming one name or a command declaring one option twice is fatal at startup, like every
/// other registry's refusal.
/// </summary>
public sealed class CommandRegistryService : ICommandRegistryService
{
    /// <summary>Every command by its name, whatever the case it is asked for in.</summary>
    private readonly Dictionary<string, ICommand> commandsByName = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The descriptors as channels receive them, fixed at startup.</summary>
    private readonly IReadOnlyList<CommandDescriptor> catalog;

    /// <summary>Indexes the installed commands by name.</summary>
    /// <exception cref="InvalidOperationException">Thrown when two commands answer to the same name or one declares itself wrongly.</exception>
    public CommandRegistryService(IEnumerable<ICommand> commands)
    {
        List<CommandDescriptor> descriptors = [];
        foreach (ICommand command in commands)
        {
            // A declaration a channel could not run as written is refused before any channel is offered it
            if (command.Descriptor.DescribeDeclarationProblem() is { } declarationProblem)
                throw new InvalidOperationException($"{command.GetType().Name}: {declarationProblem}.");

            // A name resolved to two commands would leave the channel's request to whichever registered first
            string name = command.Descriptor.Name;
            if (!commandsByName.TryAdd(name, command))
                throw new InvalidOperationException(
                    $"Command name '{name}' is claimed by both {commandsByName[name].GetType().Name} and {command.GetType().Name}.");
            // Registration order is the palette's order: the one who wires the commands decides how they are listed
            descriptors.Add(command.Descriptor);
        }
        catalog = descriptors;
    }

    /// <inheritdoc />
    public IReadOnlyList<CommandDescriptor> GetCatalog() => catalog;

    /// <inheritdoc />
    public ICommand? ResolveCommand(string name) =>
        // The index ignores case, so a channel may send the name as its user typed it
        commandsByName.GetValueOrDefault(name);
}
