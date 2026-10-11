using Alembic.Models;

namespace Alembic.Interfaces;

/// <summary>
/// Emits the project and solution files that make the archive a buildable class library on arrival.
/// </summary>
/// <remarks>It is a template like <see cref="ICodeEmitService"/>, since the shape of the project file is fixed.</remarks>
public interface ISolutionEmitService
{
    /// <summary>
    /// Emits the <c>.csproj</c> and <c>.slnx</c> that wrap the domain's generated sources.
    /// </summary>
    /// <param name="draft">The domain being packaged — an agent's namespace names the project.</param>
    /// <returns>The project file and the solution file, both at the archive's root.</returns>
    IReadOnlyList<EmittedFile> Emit(DomainDraft draft);
}
