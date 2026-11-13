using Alembic.Models;

namespace Alembic.Interfaces;

/// <summary>
/// Reads a whole domain at once and reports the defects that only become visible there.
/// Every defect it looks for is relational, such as two agents claiming one action or one value published under two names,
/// so neither <see cref="IDraftValidationService"/> nor the interview, which writes one agent at a time, can see it.
/// It advises and never blocks an export: a client who disagrees with it is often right about their own domain.
/// </summary>
public interface ICoherenceService
{
    /// <summary>
    /// The classes of defect the pass can be asked for, as its own prompt declares them.
    /// The checkboxes on Review are rendered from it, so what the client ticks, what the model is handed and what it may answer are one list.
    /// </summary>
    IReadOnlyList<CoherenceAspect> Aspects { get; }

    /// <summary>Reviews the whole domain for the classes of defect asked for.</summary>
    /// <param name="draft">The domain as it stands.</param>
    /// <param name="aspects">
    /// The <see cref="CoherenceAspect.Id"/>s to look for; the pass is composed of these and no others.
    /// An empty selection is refused rather than read as everything, since a pass nobody asked a question of spends a model call on a list.
    /// </param>
    /// <param name="resolved">Findings already applied earlier this sitting, so a run after a fix tells what is still there from what only resembles something dealt with.</param>
    /// <param name="cancellationToken">Stops the model call.</param>
    /// <returns>What the pass found; an empty report when it found nothing.</returns>
    Task<CoherenceReport> ReviewAsync(
        DomainDraft draft,
        IReadOnlyList<string> aspects,
        IReadOnlyList<ResolvedCoherenceFinding>? resolved = null,
        CancellationToken cancellationToken = default);
}

/// <summary>The outcome of one coherence pass.</summary>
/// <param name="Findings">What it found, most consequential first.</param>
/// <param name="Error">Why the pass could not run; <c>null</c> when it ran.</param>
public sealed record CoherenceReport(IReadOnlyList<CoherenceFinding> Findings, string? Error = null);

/// <summary>One thing the pass noticed.</summary>
/// <param name="Kind">What kind of incoherence it is.</param>
/// <param name="Where">The agents, intents or variables involved.</param>
/// <param name="What">The observation, in one sentence.</param>
/// <param name="Why">What it will cost at runtime.</param>
/// <param name="Fix">The change proposed, stated concretely enough to apply.</param>
public sealed record CoherenceFinding(
    string Kind,
    string Where,
    string What,
    string Why,
    string Fix);

/// <summary>A finding from an earlier pass this sitting with what the applier said it did.</summary>
/// <param name="Finding">What the earlier pass reported.</param>
/// <param name="Resolution">What <see cref="ICoherenceApplyService"/> said it changed.</param>
public sealed record ResolvedCoherenceFinding(CoherenceFinding Finding, string Resolution);