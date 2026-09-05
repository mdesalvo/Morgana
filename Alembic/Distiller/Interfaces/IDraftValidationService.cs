using Distiller.Model;

namespace Distiller.Interfaces;

/// <summary>
/// Checks a <see cref="DomainDraft"/> against everything decidable without asking a model.
/// </summary>
/// <remarks>
/// This pass runs before the recap and the ordering is the design: composing a prompt for a domain
/// that would not start is a way of lying to the client with something that looks like evidence.
/// <para>
/// What it cannot see is as important as what it can. Whether two intent descriptions overlap
/// enough to collide in the classifier, or whether an agent's Instructions contradict its
/// Formatting, is not decidable here — that is the cross-agent coherence pass and it needs a
/// model. Nothing in this service guesses at either.
/// </para>
/// </remarks>
public interface IDraftValidationService
{
    /// <summary>
    /// Runs every deterministic check over the Draft.
    /// </summary>
    /// <param name="draft">The Draft to check.</param>
    /// <returns>Findings, errors first, in the order the domain declares its elements.</returns>
    IReadOnlyList<ValidationFinding> Validate(DomainDraft draft);
}
