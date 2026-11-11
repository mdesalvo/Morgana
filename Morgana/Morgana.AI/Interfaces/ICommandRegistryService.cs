using Morgana.Contracts;

namespace Morgana.AI.Interfaces;

/// <summary>
/// The catalogue of <see cref="ICommand"/>s that this installation publishes to its channels.
/// An implementation refuses at construction two commands answering to the same name, since a channel resolving a typed
/// name must never find two candidates. It refuses a command whose DescribeDeclarationProblem names a fault just the same.
/// </summary>
public interface ICommandRegistryService
{
    /// <summary>The published commands in the order that a palette lists them; empty when none is installed.</summary>
    IReadOnlyList<CommandDescriptor> GetCatalog();

    /// <summary>The command answering to <paramref name="name"/> ignoring case; null when none does.</summary>
    ICommand? ResolveCommand(string name);
}