using Alembic.Models;

namespace Alembic.Interfaces;

/// <summary>
/// Renders a <see cref="DomainDraft"/> back into the <c>agents.json</c> Morgana loads, equivalent to what was imported.
/// Indentation, key order inside AdditionalProperties and explicit defaults may differ, since Morgana reads none of them; no intent, prompt, tool,
/// parameter, scope or shared flag may. That is what lets a client upload ten agents to add an eleventh and get the ten back untouched.
/// </summary>
public interface IDraftExportService
{
    /// <summary>Renders the Draft as the <c>agents.json</c> the client downloads.</summary>
    /// <param name="draft">The Draft to render.</param>
    /// <returns>UTF-8 JSON bytes.</returns>
    byte[] Export(DomainDraft draft);
}