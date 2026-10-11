using Alembic.Models;

namespace Alembic.Interfaces;

/// <summary>
/// Applies one coherence finding's own proposed fix to the Draft.
/// </summary>
/// <remarks>It runs only when the client presses Apply, since the coherence pass itself stays advisory.</remarks>
public interface ICoherenceApplyService
{
    /// <summary>
    /// Applies one finding's fix.
    /// </summary>
    /// <param name="draft">The domain the finding was read against, changed in place.</param>
    /// <param name="finding">The finding to apply, exactly as shown to the client.</param>
    /// <param name="cancellationToken">Stops the run when the client leaves the page.</param>
    /// <returns>Whether the fix was declared applied and what changed.</returns>
    Task<CoherenceApplyResult> ApplyAsync(DomainDraft draft, CoherenceFinding finding, CancellationToken cancellationToken = default);
}

/// <summary>
/// The outcome of applying one finding.
/// </summary>
/// <param name="Applied">Whether the pass declared the fix done.</param>
/// <param name="Summary">What changed, in the client's own domain words, or why not.</param>
public sealed record CoherenceApplyResult(bool Applied, string Summary);
