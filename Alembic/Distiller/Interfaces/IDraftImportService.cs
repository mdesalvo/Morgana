using Distiller.Model;

namespace Distiller.Interfaces;

/// <summary>
/// Turns an uploaded <c>agents.json</c> into a <see cref="DomainDraft"/>.
/// </summary>
/// <remarks>
/// Import is the only way an existing domain enters Alembic: there is no filesystem to read from,
/// by design, because at runtime Alembic lives wherever the client deployed it.
/// </remarks>
public interface IDraftImportService
{
    /// <summary>
    /// Parses an <c>agents.json</c> stream into a Draft whose every element is marked
    /// <see cref="Provenance.Imported"/>.
    /// </summary>
    /// <param name="agentsJson">The uploaded configuration.</param>
    /// <param name="fileName">Name of the uploaded file, recorded on the Draft for the migration report.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The Draft, plus what the import wants the client to know about it.</returns>
    Task<DraftImportResult> ImportAsync(
        Stream agentsJson,
        string fileName,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Outcome of an import: either a Draft, or the reason there isn't one.
/// </summary>
/// <param name="Draft">The imported Draft, or <c>null</c> when <paramref name="Error"/> is set.</param>
/// <param name="Notices">
/// What the client should know about what was read: counts and anything Alembic met but does not
/// model. Never a validation verdict — checking whether the domain is coherent is a separate pass
/// with a separate purpose and conflating the two would report a malformed domain as a bad file.
/// </param>
/// <param name="Error">Why the file could not be read at all, or <c>null</c> on success.</param>
public sealed record DraftImportResult(
    DomainDraft? Draft,
    IReadOnlyList<string> Notices,
    string? Error)
{
    /// <summary>
    /// Whether a Draft came out of it.
    /// </summary>
    public bool Succeeded => Draft is not null;
}
