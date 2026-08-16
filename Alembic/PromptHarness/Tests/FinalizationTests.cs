using Distiller.Interfaces;
using Distiller.Model;
using PromptHarness.Fixtures;
using PromptHarness.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace PromptHarness.Tests;

/// <summary>
/// Tests Alembic's finalization step — deterministic validation and the emitted archive — against
/// the Draft one Bistro Luna interview leaves behind, shared across every test here by
/// <see cref="BistroLunaInterviewFixture"/>.
/// </summary>
/// <remarks>
/// This is the layer that would have caught, on its own and without a human noticing anything at
/// runtime, two of the four defects a live session against this exact fixture found: the fallback
/// intent reported as "Added" in the migration report, and the mock's duplicated
/// <c>[ProvidesToolForIntent]</c> attribute breaking the build. Both are asserted here directly, by
/// name, so neither can regress silently.
/// </remarks>
public sealed class FinalizationTests : IClassFixture<BistroLunaInterviewFixture>
{
    private readonly BistroLunaInterviewFixture interviewed;

    public FinalizationTests(BistroLunaInterviewFixture interviewed) => this.interviewed = interviewed;

    [Fact]
    public void Deterministic_validation_reports_no_errors()
    {
        IDraftValidationService validation = interviewed.Services.GetRequiredService<IDraftValidationService>();
        List<ValidationFinding> errors = [.. validation.Validate(interviewed.Draft).Where(f => f.Severity == FindingSeverity.Error)];

        Assert.True(errors.Count == 0,
            $"Expected no validation errors, found:\n{string.Join('\n', errors.Select(e => $"- {e.Where}: {e.Message}"))}\n{interviewed.Driven}");
    }

    [Fact]
    public void Migration_report_never_calls_the_fallback_intent_new()
    {
        IMigrationReportService migrationReport = interviewed.Services.GetRequiredService<IMigrationReportService>();
        MigrationReport report = migrationReport.Build(interviewed.Draft);

        Assert.DoesNotContain(report.Entries, e =>
            string.Equals(e.Where, DomainDraft.FallbackIntent, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Emitted_archive_compiles_clean()
    {
        IAssetPackageService packager = interviewed.Services.GetRequiredService<IAssetPackageService>();
        byte[] archive = await packager.BuildAsync(interviewed.Draft, includeScenarios: false);

        ArchiveCompileResult result = await ArchiveCompiler.ExtractAndBuildAsync(archive);

        Assert.True(result.Succeeded,
            $"Archive did not compile (exit code {result.ExitCode}):\n{result.Output}\n{interviewed.Driven}");
    }
}
