using Alembic.Models;

namespace Alembic.Interfaces;

/// <summary>
/// Builds the single archive a client downloads at the end: configuration, sources and the report.
/// </summary>
/// <remarks>One archive holds every piece, since an <c>agents.json</c> and the C# beside it are only correct together.</remarks>
public interface IAssetPackageService
{
    /// <summary>
    /// Packages the whole domain.
    /// </summary>
    /// <param name="draft">The domain to package.</param>
    /// <param name="progress">Reports each file as it is written, since the mocks are LLM calls.</param>
    /// <param name="cancellationToken">Stops the packaging when the client leaves the page.</param>
    /// <returns>The zip archive the client downloads.</returns>
    Task<byte[]> BuildAsync(
        DomainDraft draft,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);
}
