using Alembic.Models;

namespace Alembic.Interfaces;

/// <summary>
/// Builds the single archive a client downloads at the end: configuration, sources and the report.
/// </summary>
/// <remarks>
/// One download and not several, because the pieces are only correct together. The
/// generated C# declares the toolkit and the <c>agents.json</c> beside it holds the prose, so two
/// separate downloads are an invitation to take only one of them.
/// </remarks>
public interface IAssetPackageService
{
    /// <summary>
    /// Packages the whole domain.
    /// </summary>
    /// <param name="draft">The domain to package.</param>
    /// <param name="progress">Reports each file as it is written, since the mocks are LLM calls.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A zip archive.</returns>
    Task<byte[]> BuildAsync(
        DomainDraft draft,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);
}
