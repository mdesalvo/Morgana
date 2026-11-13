using Alembic.Models;

namespace Alembic.Interfaces;

/// <summary>
/// Applies one coherence finding's own proposed fix to the Draft, once the client presses Apply.
/// The coherence pass stays advisory: this pass carries out a decision the client already took and is never a second opinion.
/// </summary>
public interface ICoherenceApplyService
{
    /// <summary>Applies one finding's fix, changing the Draft in place.</summary>
    /// <param name="draft">The domain the finding was read against, changed in place.</param>
    /// <param name="finding">The finding to apply, exactly as shown to the client.</param>
    /// <param name="cancellationToken">Stops the model call.</param>
    Task<CoherenceApplyResult> ApplyAsync(DomainDraft draft, CoherenceFinding finding, CancellationToken cancellationToken = default);
}

/// <summary>The outcome of applying one finding.</summary>
/// <param name="Applied">Whether the pass declared the fix done.</param>
/// <param name="Summary">What changed, in the client's own domain words; otherwise why nothing did.</param>
public sealed record CoherenceApplyResult(bool Applied, string Summary);