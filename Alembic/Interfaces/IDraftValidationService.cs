using Alembic.Models;

namespace Alembic.Interfaces;

/// <summary>
/// Checks a <see cref="DomainDraft"/> against everything decidable without asking a model.
/// </summary>
/// <remarks>What needs a model, such as overlapping descriptions, belongs to <see cref="ICoherenceService"/>.</remarks>
public interface IDraftValidationService
{
    /// <summary>
    /// Runs every deterministic check over the Draft.
    /// </summary>
    /// <param name="draft">The Draft to check.</param>
    /// <returns>Findings, errors first, in the order the domain declares its elements.</returns>
    IReadOnlyList<ValidationFinding> Validate(DomainDraft draft);
}
