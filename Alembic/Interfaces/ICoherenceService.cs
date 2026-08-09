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
/// another expects as <c>customerCode</c>, an agent whose Target promises what a neighbour owns.
/// No per-agent pass can see any of it.
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
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What the pass found, or an empty report when it found nothing.</returns>
    Task<CoherenceReport> ReviewAsync(DomainDraft draft, CancellationToken cancellationToken = default);
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
