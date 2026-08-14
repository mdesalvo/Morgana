using Alembic.Model;

namespace Alembic.Interfaces;

/// <summary>
/// Reads a whole domain at once and reports what only becomes visible there.
/// </summary>
/// <remarks>
/// <para>
/// This is the half of reviewing a domain that <see cref="IDraftValidationService"/> explicitly
/// cannot do. That one decides everything decidable by reading the Draft — a name that cannot be a
/// C# identifier, an intent nothing routes to — and asks no model, because none would help. Whether
/// two intent descriptions overlap enough to collide in the classifier is the opposite kind of
/// question: it is about meaning, it has no mechanical answer, and it is the single most expensive
/// defect a multi-agent domain can carry.
/// </para>
/// <para>
/// It runs over the domain and never over one agent. Every defect it looks for is <b>relational</b>
/// and therefore invisible to the interview, which settles one agent at a time by construction: two
/// agents that both claim to cancel something, a value one agent publishes as <c>userId</c> and
/// another expects as <c>customerCode</c>, an agent whose Target promises what a neighbour owns,
/// two toolkits that reach the same system for the same purpose under two different shapes. No
/// per-agent pass can see any of it.
/// </para>
/// <para>
/// It is advisory, and stays advisory. Nothing here blocks an export: a judgement about meaning is
/// not a verdict, and a client who disagrees with it is often right about their own domain.
/// </para>
/// </remarks>
public interface ICoherenceService
{
    /// <summary>
    /// Reviews the whole domain.
    /// </summary>
    /// <param name="draft">The domain as it stands.</param>
    /// <param name="resolved">
    /// Findings already applied earlier this sitting, if any. Each run is otherwise a one-shot read
    /// with no memory of the last one, so a client who fixed something and pressed "Run it again"
    /// would face a pass with no way to tell "still there" from "looks the same as something I
    /// already dealt with" — this is what lets it tell the difference instead of re-reporting on
    /// sight.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What the pass found, or an empty report when it found nothing.</returns>
    Task<CoherenceReport> ReviewAsync(
        DomainDraft draft,
        IReadOnlyList<ResolvedCoherenceFinding>? resolved = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The outcome of one coherence pass.
/// </summary>
/// <param name="Findings">What it found, most consequential first.</param>
/// <param name="Error">Why the pass could not run, or <c>null</c>.</param>
public sealed record CoherenceReport(IReadOnlyList<CoherenceFinding> Findings, string? Error = null);

/// <summary>
/// One thing the pass noticed.
/// </summary>
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

/// <summary>
/// A finding from an earlier pass this sitting, and what the client's own applier said it did.
/// </summary>
/// <param name="Finding">What the earlier pass reported.</param>
/// <param name="Resolution">What <see cref="ICoherenceApplyService"/> said it changed.</param>
public sealed record ResolvedCoherenceFinding(CoherenceFinding Finding, string Resolution);
