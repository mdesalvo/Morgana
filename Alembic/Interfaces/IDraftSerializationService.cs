using Alembic.Models;

namespace Alembic.Interfaces;

/// <summary>
/// Reads and writes <c>alembic-draft.json</c>, the interview's own save file.
/// An interview over a real domain does not fit in one sitting and Alembic holds no database, so the Draft has to be something the client takes away and brings back.
/// It is not <c>agents.json</c>: it carries provenance, the C# facts a configuration cannot express and half-answered elements, none of which Morgana loads.
/// </summary>
public interface IDraftSerializationService
{
    /// <summary>Renders a Draft as the JSON the client downloads.</summary>
    /// <param name="draft">The Draft to save.</param>
    /// <returns>UTF-8 JSON bytes.</returns>
    byte[] Serialize(DomainDraft draft);

    /// <summary>Reads back a Draft the client saved earlier.</summary>
    /// <param name="draftJson">The uploaded <c>alembic-draft.json</c>.</param>
    /// <param name="cancellationToken">Stops reading the stream.</param>
    /// <returns>The Draft; <c>null</c> when the file is not one.</returns>
    Task<DomainDraft?> DeserializeAsync(Stream draftJson, CancellationToken cancellationToken = default);
}