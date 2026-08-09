using Alembic.Model;

namespace Alembic.Interfaces;

/// <summary>
/// Builds the single archive a client downloads at the end: configuration, sources, and the report.
/// </summary>
/// <remarks>
/// One download and not several, because the pieces are only correct together. An
/// <c>agents.json</c> whose toolkit has moved on from the C# beside it is a startup failure, and
/// two separate downloads is an invitation to take one of them.
/// </remarks>
public interface IAssetPackageService
{
    /// <summary>
    /// Packages the whole domain.
    /// </summary>
    /// <param name="draft">The domain to package.</param>
    /// <param name="includeScenarios">
    /// Whether to author the starter PromptHarness scenarios. One further LLM call per agent, and
    /// worth stating as a choice for that reason alone — but a domain that leaves without them is a
    /// domain whose prose will be edited with nothing watching.
    /// </param>
    /// <param name="progress">Reports each file as it is written, since the mocks are LLM calls.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A zip archive.</returns>
    Task<byte[]> BuildAsync(
        DomainDraft draft,
        bool includeScenarios = true,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);
}
