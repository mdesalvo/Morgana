using Alembic.Model;

namespace Alembic.Interfaces;

/// <summary>
/// Applies one coherence finding's own proposed fix to the Draft.
/// </summary>
/// <remarks>
/// The coherence pass itself stays advisory — <see cref="ICoherenceService"/> only ever answers
/// JSON, and a domain expert who disagrees with it is often right about their own business. This is
/// the opposite half, reached only when the client presses "Apply": having read the finding and its
/// fix, they are the ones deciding to act on it, and this pass is the hand that carries out an
/// instruction already given rather than a second opinion.
/// </remarks>
public interface ICoherenceApplyService
{
    /// <summary>
    /// Applies one finding's fix.
    /// </summary>
    /// <param name="draft">The domain the finding was read against — mutated in place.</param>
    /// <param name="finding">The finding to apply, exactly as shown to the client.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<CoherenceApplyResult> ApplyAsync(DomainDraft draft, CoherenceFinding finding, CancellationToken cancellationToken = default);
}

/// <summary>
/// The outcome of applying one finding.
/// </summary>
/// <param name="Applied">Whether the pass declared the fix done.</param>
/// <param name="Summary">What changed, in the client's own domain words, or why not.</param>
public sealed record CoherenceApplyResult(bool Applied, string Summary);
