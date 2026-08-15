using Alembic.Model;

namespace Alembic.Interfaces;

/// <summary>
/// Emits the project and solution files that turn the archive into a buildable class library on
/// arrival, instead of loose sources the client must first shelter inside a project of their own.
/// </summary>
/// <remarks>
/// Deterministic template, same as <see cref="ICodeEmitService"/> and for the same reason: the
/// shape of a .csproj referencing the <c>Morgana.AI</c> package is fixed and known at compile time,
/// so a generator would only approximate what a template reproduces exactly.
/// </remarks>
public interface ISolutionEmitService
{
    /// <summary>
    /// Emits the <c>.csproj</c> and <c>.slnx</c> that wrap the domain's generated sources.
    /// </summary>
    /// <param name="draft">The domain being packaged — an agent's namespace names the project.</param>
    /// <returns>The project file and the solution file, both at the archive's root.</returns>
    IReadOnlyList<EmittedFile> Emit(DomainDraft draft);
}
