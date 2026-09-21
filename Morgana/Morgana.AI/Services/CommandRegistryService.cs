using Morgana.AI.Interfaces;
using Morgana.Contracts;

namespace Morgana.AI.Services;

/// <summary>
/// Publishes every <see cref="ICommand"/> registered in DI, in registration order.
/// A clash between two names or aliases is fatal at startup, like every other registry's refusal.
/// </summary>
public sealed class CommandRegistryService : ICommandRegistryService
{
    /// <summary>Every name and alias, lowercase, mapped to the command it resolves to.</summary>
    private readonly Dictionary<string, ICommand> commandsByName = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The descriptors as channels receive them, fixed at startup.</summary>
    private readonly IReadOnlyList<CommandDescriptor> catalog;

    /// <summary>Indexes the installed commands by name and alias.</summary>
    /// <exception cref="InvalidOperationException">Thrown when two commands answer to the same name or alias.</exception>
    public CommandRegistryService(IEnumerable<ICommand> commands)
    {
        List<CommandDescriptor> descriptors = [];
        foreach (ICommand command in commands)
        {
            // Names and aliases share one space: an alias shadowing another command's name is the same clash
            foreach (string name in (IEnumerable<string>)[command.Descriptor.Name, .. command.Descriptor.Aliases ?? []])
            {
                if (!commandsByName.TryAdd(name, command))
                    throw new InvalidOperationException(
                        $"Command name '{name}' is claimed by both {commandsByName[name].GetType().Name} and {command.GetType().Name}.");
            }
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
