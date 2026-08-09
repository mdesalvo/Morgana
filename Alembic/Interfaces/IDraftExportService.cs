using Alembic.Model;

namespace Alembic.Interfaces;

/// <summary>
/// Renders a <see cref="DomainDraft"/> back into the <c>agents.json</c> Morgana loads.
/// </summary>
/// <remarks>
/// Export closes the loop that import opens, and the pair carries the invariant everything later
/// stands on: <b>a configuration that goes in comes back out equivalent</b>. Equivalent, not
/// byte-identical — indentation, key order inside AdditionalProperties and explicitly-written
/// defaults are free to differ, because none of them changes what Morgana reads. What may not
/// differ is a single intent, prompt, tool, parameter, scope or shared flag.
/// <para>
/// That invariant is what makes the interview safe to build on top: a client uploading a domain of
/// ten agents to add an eleventh gets the other ten back untouched, and Alembic does not need to
/// understand them to promise it.
/// </para>
/// </remarks>
public interface IDraftExportService
{
    /// <summary>
    /// Renders the Draft as the <c>agents.json</c> the client downloads.
    /// </summary>
    /// <param name="draft">The Draft to render.</param>
    /// <returns>UTF-8 JSON bytes.</returns>
    byte[] Export(DomainDraft draft);
}
