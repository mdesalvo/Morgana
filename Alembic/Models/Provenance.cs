namespace Alembic.Models;

/// <summary>
/// Where a piece of the Draft came from.
/// </summary>
/// <remarks>It lets the migration report name what Alembic touched while the round-trip invariant alone preserves the rest.</remarks>
public enum Provenance
{
    /// <summary>
    /// Read from the uploaded configuration and not touched since.
    /// </summary>
    Imported,

    /// <summary>
    /// Read from the uploaded configuration, then modified in this session.
    /// </summary>
    Revised,

    /// <summary>
    /// Created in this session; it exists in no uploaded file.
    /// </summary>
    Authored
}
