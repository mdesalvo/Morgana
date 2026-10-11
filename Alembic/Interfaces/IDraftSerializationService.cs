using Alembic.Models;

namespace Alembic.Interfaces;

/// <summary>
/// Reads and writes <c>alembic-draft.json</c>, the interview's own save file.
/// </summary>
/// <remarks>The file carries provenance and the C# facts that <c>agents.json</c> cannot express, so it is never loaded by Morgana.</remarks>
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
    /// <param name="cancellationToken">Stops the read when the client leaves the page.</param>
    /// <returns>The Draft, or <c>null</c> if the file is not one.</returns>
    Task<DomainDraft?> DeserializeAsync(Stream draftJson, CancellationToken cancellationToken = default);
}
