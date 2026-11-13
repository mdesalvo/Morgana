using Alembic.Models;

namespace Alembic.Interfaces;

/// <summary>
/// Works out what a business does by reading a domain somebody else finished.
/// A bare <c>agents.json</c> carries none of the interview's memory of the trade, so a step reopened over one would know what was decided and nothing of the shop it was decided for.
/// What it writes is marked as read rather than said wherever it is used, since a reading presented as the client's own words would make the interview worse than the empty state.
/// </summary>
public interface IDomainReadingService
{
    /// <summary>Reads the uploaded domain and writes what it says about the client's work onto the Draft.</summary>
    /// <param name="draft">The imported domain, changed in place: every agent gains what its own prose says about the work it does.</param>
    /// <param name="cancellationToken">Cancels the underlying completion.</param>
    /// <returns>How many facts were written down with what went wrong where nothing was.</returns>
    Task<DomainReading> ReadAsync(DomainDraft draft, CancellationToken cancellationToken = default);
}