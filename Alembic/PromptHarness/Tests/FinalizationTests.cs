using Distiller.Interfaces;
using Distiller.Model;
using PromptHarness.Fixtures;
using PromptHarness.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace PromptHarness.Tests;

/// <summary>
/// Tests Alembic's finalization step — deterministic validation and the emitted archive — against
/// the Draft one Bistro Luna interview leaves behind, shared with every other class in
/// <see cref="BistroLunaCollection"/> rather than driven again here.
/// </summary>
/// <remarks>
/// This is the layer that would have caught, on its own and without a human noticing anything at
/// runtime, two of the four defects a live session against this exact fixture found: the fallback
/// intent reported as "Added" in the migration report and the mock's duplicated
/// <c>[ProvidesToolForIntent]</c> attribute breaking the build. Both are asserted here directly, by
/// name, so neither can regress silently.
/// </remarks>
[Collection(BistroLunaCollection.Name)]
[Trait("Stage", "Finalization")]
public sealed class FinalizationTests
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
        byte[] archive = await packager.BuildAsync(
            interviewed.Draft, includeScenarios: false, cancellationToken: TestContext.Current.CancellationToken);

        ArchiveCompileResult result = await ArchiveCompiler.ExtractAndBuildAsync(
            archive, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded,
            $"Archive did not compile (exit code {result.ExitCode}):\n{result.Output}\n{interviewed.Driven}");
    }

    // Compiling clean proves the generated .g.cs and the model-authored mock agree with each other
    // and with Morgana.AI's own types — it proves nothing about whether what came out is the
    // SPECIFIC agent and toolkit this Draft asked for. A tool class built against the wrong intent
    // name, or a partial method the client's half happens to compile without ever implementing, is
    // invisible to the compiler. This reflects over the built assembly the way
    // HandlesIntentAgentRegistryService and MorganaToolAdapter.AddTool would at a real Morgana's
    // startup, without booting one.
    [Fact]
    public async Task Emitted_assembly_declares_every_agent_and_tool_the_draft_promises()
    {
        IAssetPackageService packager = interviewed.Services.GetRequiredService<IAssetPackageService>();
        byte[] archive = await packager.BuildAsync(
            interviewed.Draft, includeScenarios: false, cancellationToken: TestContext.Current.CancellationToken);

        ArchiveCompileResult result = await ArchiveCompiler.ExtractAndBuildAsync(
            archive, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded,
            $"Archive did not compile (exit code {result.ExitCode}):\n{result.Output}\n{interviewed.Driven}");
        Assert.True(result.AssemblyPath is not null,
            $"Build succeeded but the emitted assembly was not found on disk.\n{result.Output}");

        IReadOnlyList<AgentExpectation> expectations =
        [
            .. interviewed.Draft.Agents.Select(agent => new AgentExpectation(
                agent.ID!,
                [.. agent.Tools.Where(t => !string.IsNullOrWhiteSpace(t.Name)).Select(t => t.Name!)]))
        ];

        IReadOnlyList<string> missing = EmittedAssemblyInspector.FindMissing(result.AssemblyPath!, expectations);

        Assert.True(missing.Count == 0,
            $"The emitted assembly does not declare what the Draft promises:\n" +
            $"{string.Join('\n', missing.Select(m => $"- {m}"))}\n{interviewed.Driven}");
    }
}
