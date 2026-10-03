using PromptHarness.Infrastructure.Engine;
using PromptHarness.Infrastructure.Wiring;
using Xunit;

namespace PromptHarness.Tests;

/// <summary>
/// The Guard prompt itself, on a live moderation LLM call — never exercised meaningfully by the
/// rest of the suite, since the pipeline runs with the guard rail off by default.
/// </summary>
/// <remarks>
/// <para><strong>Requires <c>Harness:EnableUserGuardrail=true</c></strong> — the guard rail is a
/// whole-process boot flag (<c>MorganaHostFixture.ApplyHostEnvironment</c>), set once before the
/// single assembly-wide host starts, with no per-scenario override. Run this class on its own:</para>
/// <code>Harness__EnableUserGuardrail=true dotnet test PromptHarness.csproj --filter "FullyQualifiedName~PromptHarness.Tests.UserGuardTests"</code>
///
/// <para>Running it with the flag left off does <em>not</em> silently skip: per <c>GuardActor</c>,
/// a disabled guard rail short-circuits to <c>GuardRailResult(true, null)</c> without ever calling
/// the LLM, so <c>userguard-rejects-abusive-message</c> fails loudly and correctly — <c>compliant</c>
/// can structurally never come back <c>false</c> — rather than passing by accident.</para>
/// </remarks>
public sealed class UserGuardTests
{
    /// <summary>The live host, shared with every other test class in the assembly.</summary>
    private readonly MorganaHostFixture fixture;

    public UserGuardTests(MorganaHostFixture fixture) => this.fixture = fixture;

    [Theory]
    [InlineData("userguard-rejects-abusive-message")]
    [InlineData("userguard-allows-good-faith-difficult-topic")]
    [InlineData("userguard-rejects-authority-impersonation")]
    [InlineData("userguard-rejects-forced-disclosure")]
    [InlineData("userguard-rejects-personality-engineering")]
    public async Task UserGuard_scenario_holds(string scenarioId)
    {
        ScenarioOutcome outcome = await fixture.Runner.RunAsync(scenarioId);

        Assert.True(outcome.Passed, outcome.Report());
    }
}
