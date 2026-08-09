namespace Alembic.Model;

/// <summary>
/// Where a piece of the Draft came from.
/// </summary>
/// <remarks>
/// Provenance exists so Alembic rewrites only what it owns. A client uploading an
/// <c>agents.json</c> with ten agents in order to add an eleventh must get the other ten back
/// unchanged, and must be told — in the migration report — exactly which of them Alembic touched.
/// <para>
/// It is deliberately NOT the mechanism that preserves untouched content: that is the round-trip
/// invariant (import then export returns an equivalent file), which holds regardless of provenance.
/// Provenance is what makes the <em>reporting</em> honest, and what later decides whether a C#
/// asset is worth emitting at all.
/// </para>
/// </remarks>
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
