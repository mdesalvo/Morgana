namespace Alembic.Models;

/// <summary>
/// Where a piece of the Draft came from, so the migration report tells the client exactly which of their agents Alembic touched.
/// It does not preserve untouched content: the round-trip invariant does that whatever the provenance.
/// </summary>
public enum Provenance
{
    /// <summary>Read from the uploaded configuration and not touched since.</summary>
    Imported,

    /// <summary>Read from the uploaded configuration, then modified in this session.</summary>
    Revised,

    /// <summary>Created in this session; it exists in no uploaded file.</summary>
    Authored
}