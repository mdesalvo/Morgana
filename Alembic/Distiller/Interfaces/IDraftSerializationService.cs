using Distiller.Model;

namespace Distiller.Interfaces;

/// <summary>
/// Reads and writes <c>alembic-draft.json</c>, the interview's own save file.
/// </summary>
/// <remarks>
/// A functional interview over a real domain — three passes, one of them per tool — does not fit in
/// one sitting, and Alembic holds no database and no filesystem. The Draft therefore has to be
/// something the client can take away and bring back: this is that format.
/// <para>
/// It is not <c>agents.json</c> and is not meant to be. It carries provenance, the C# facts the
/// configuration cannot express, and half-answered elements — none of which belong in a file
/// Morgana has to load.
/// </para>
/// </remarks>
public interface IDraftSerializationService
{
    /// <summary>
    /// Renders a Draft as the JSON the client downloads.
    /// </summary>
    /// <param name="draft">The Draft to save.</param>
    /// <returns>UTF-8 JSON bytes.</returns>
    byte[] Serialize(DomainDraft draft);

    /// <summary>
    /// Reads back a Draft the client saved earlier.
    /// </summary>
    /// <param name="draftJson">The uploaded <c>alembic-draft.json</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The Draft, or <c>null</c> if the file is not one.</returns>
    Task<DomainDraft?> DeserializeAsync(Stream draftJson, CancellationToken cancellationToken = default);
}
