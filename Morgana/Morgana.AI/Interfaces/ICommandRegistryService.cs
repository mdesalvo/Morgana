using Morgana.Contracts;

namespace Morgana.AI.Interfaces;

/// <summary>
/// The catalogue of <see cref="ICommand"/>s this installation publishes to its channels.
/// Implementations must refuse, at construction, two commands answering to the same name: a channel
/// resolving a typed name must never find two candidates. They must refuse a command whose
/// <see cref="CommandDescriptor.DescribeDeclarationProblem"/> names a fault just the same.
/// Default implementation: CommandRegistryService collects every command registered in DI.
/// </summary>
public interface ICommandRegistryService
{
    /// <summary>The published commands in the order a palette should list them. Empty when none is installed.</summary>
    IReadOnlyList<CommandDescriptor> GetCatalog();

    /// <summary>Finds the command answering to <paramref name="name"/>, ignoring case; null when none does.</summary>
    ICommand? ResolveCommand(string name);
}