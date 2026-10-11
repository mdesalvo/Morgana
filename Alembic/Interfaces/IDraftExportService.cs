using Alembic.Models;

namespace Alembic.Interfaces;

/// <summary>
/// Renders a <see cref="DomainDraft"/> back into the <c>agents.json</c> Morgana loads.
/// </summary>
/// <remarks>What goes in comes back out equivalent: no intent, prompt or workflow may differ while formatting and key order may.</remarks>
public interface IDraftExportService
{
    /// <summary>
    /// Renders the Draft as the <c>agents.json</c> the client downloads.
    /// </summary>
    /// <param name="draft">The Draft to render.</param>
    /// <returns>UTF-8 JSON bytes.</returns>
    byte[] Export(DomainDraft draft);
}
