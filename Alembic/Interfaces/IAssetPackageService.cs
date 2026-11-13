using Alembic.Models;

namespace Alembic.Interfaces;

/// <summary>
/// Builds the single archive a client downloads at the end: configuration, sources and the report.
/// One download, because the C# declares the toolkit that the <c>agents.json</c> beside it writes prose for: two downloads invite taking one.
/// </summary>
public interface IAssetPackageService
{
    /// <summary>Packages the whole domain as a zip archive.</summary>
    /// <param name="draft">The domain to package.</param>
    /// <param name="progress">Reports each file as it is written, since every mock is a model call of its own.</param>
    /// <param name="cancellationToken">Stops the packaging, mocks included.</param>
    /// <returns>The zip archive.</returns>
    Task<byte[]> BuildAsync(
        DomainDraft draft,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);
}