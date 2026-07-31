using PromptHarness.Infrastructure.Engine;
using PromptHarness.Infrastructure.Wiring;
using Xunit;

namespace PromptHarness.Tests;

/// <summary>
/// The Guard prompt itself, on a live moderation LLM call — never exercised meaningfully by the
/// rest of the suite, since the pipeline runs with the guard rail off by default.
/// </summary>
/// <remarks>
/// <para><strong>Requires <c>Harness:EnableGuardrail=true</c></strong> — the guard rail is a
/// whole-process boot flag (<c>MorganaHostFixture.ApplyHostEnvironment</c>), set once before the
/// single assembly-wide host starts, with no per-scenario override. Run this class on its own:</para>
/// <code>Harness__EnableGuardrail=true dotnet test PromptHarness.csproj --filter "FullyQualifiedName~GuardTests"</code>
///
/// <para>Running it with the flag left off does <em>not</em> silently skip: per <c>GuardActor</c>,
/// a disabled guard rail short-circuits to <c>GuardRailResult(true, null)</c> without ever calling
/// the LLM, so <c>guard-rejects-abusive-message</c> fails loudly and correctly — <c>compliant</c>
/// can structurally never come back <c>false</c> — rather than passing by accident.</para>
/// </remarks>
public sealed class GuardTests
{
    /// <summary>The live host, shared with every other test class in the assembly.</summary>
    private readonly MorganaHostFixture fixture;

    public GuardTests(MorganaHostFixture fixture) => this.fixture = fixture;

    [Theory]
    [InlineData("guard-rejects-abusive-message")]
    [InlineData("guard-allows-good-faith-difficult-topic")]
    public async Task Guard_scenario_holds(string scenarioId)
    {
        ScenarioOutcome outcome = await fixture.Runner.RunAsync(scenarioId);

        Assert.True(outcome.Passed, outcome.Report());
    }
}
