using Alembic.Models;

namespace Alembic.Interfaces;

/// <summary>
/// Emits the project and solution files that make the archive a buildable class library on arrival, by deterministic template.
/// The shape of a project referencing the <c>Morgana.AI</c> package is fixed and known, so a template reproduces it exactly.
/// </summary>
public interface ISolutionEmitService
{
    /// <summary>Emits the <c>.csproj</c> and <c>.slnx</c> that wrap the domain's generated sources.</summary>
    /// <param name="draft">The domain being packaged, whose agents' namespace names the project.</param>
    /// <returns>The project file and the solution file, both at the archive's root.</returns>
    IReadOnlyList<EmittedFile> Emit(DomainDraft draft);
}