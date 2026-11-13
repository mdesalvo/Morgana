using Alembic.Models;

namespace Alembic.Interfaces;

/// <summary>
/// Checks a <see cref="DomainDraft"/> against everything decidable without asking a model.
/// It runs before the recap, since composing a prompt for a domain that would not start shows the client something that only looks like evidence.
/// Whether two descriptions collide in the classifier or two sections of an agent contradict each other is the coherence pass's, which needs a model.
/// </summary>
public interface IDraftValidationService
{
    /// <summary>Runs every deterministic check over the Draft.</summary>
    /// <param name="draft">The Draft to check.</param>
    /// <returns>Findings, errors first, in the order the domain declares its elements.</returns>
    IReadOnlyList<ValidationFinding> Validate(DomainDraft draft);
}