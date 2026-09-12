using Distiller.Model;

namespace Distiller.Interfaces;

/// <summary>
/// Works out what a business does by reading a domain somebody else finished.
/// </summary>
/// <remarks>
/// <para>
/// A save file carries the interview's own memory of the trade; a bare <c>agents.json</c> carries
/// none. What it holds is finished prose, written for agents, with nothing left of the conversation
/// that produced it — so an interview reopened over one starts every step knowing what was decided
/// and nothing about the shop it was decided for, which is the state that has a step asking a baker
/// what a system ought to be able to check.
/// </para>
/// <para>
/// What it writes is marked as read rather than said, everywhere it is used. The client never told
/// Alembic any of it and a reading presented as their own words is the one way this can make an
/// interview worse than the empty state it replaces.
/// </para>
/// </remarks>
public interface IDomainReadingService
{
    /// <summary>
    /// Reads the uploaded domain and writes what it says about the client's work onto the Draft.
    /// </summary>
    /// <param name="draft">The imported domain, changed in place: every agent gains what its own prose says about the work it does.</param>
    /// <param name="cancellationToken">Cancels the underlying completion.</param>
    /// <returns>How many facts were written down, and what went wrong where nothing was.</returns>
    Task<DomainReading> ReadAsync(DomainDraft draft, CancellationToken cancellationToken = default);
}
