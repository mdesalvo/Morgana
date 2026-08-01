using PromptHarness.Infrastructure.Engine;
using PromptHarness.Infrastructure.Wiring;
using Xunit;

namespace PromptHarness.Tests;

/// <summary>
/// The summarization prompt, which no other scenario has ever exercised: the default trigger (21
/// non-system messages — <c>SummarizationTargetCount</c> 8 + <c>SummarizationThreshold</c> 12) sits
/// far above what any scripted conversation reaches.
/// </summary>
/// <remarks>
/// <para><strong>Requires a lowered trigger at boot</strong> — like the guard rail, the reducer's
/// configuration is process-wide for the single assembly-shared host, so lowering it would silently
/// change every other class's few-turn conversations too. Run this class on its own:</para>
/// <code>Harness__SummarizationThreshold=4 Harness__SummarizationTargetCount=4 dotnet test PromptHarness.csproj --filter "FullyQualifiedName~SummarizationTests"</code>
///
/// <para>At 4+4 the trigger is 8 messages — comfortably below the ~16 the scripted scenario's first
/// two turns accumulate, so the reduction fires at the start of the third turn, after the material
/// worth compressing (and worth losing, if the prompt fails) already exists.</para>
/// </remarks>
public sealed class SummarizationTests
{
    /// <summary>The live host, shared with every other test class in the assembly.</summary>
    private readonly MorganaHostFixture fixture;

    public SummarizationTests(MorganaHostFixture fixture) => this.fixture = fixture;

    [Theory]
    [InlineData("summarization-preserves-invoice-details")]
    public async Task Summarization_scenario_holds(string scenarioId)
    {
        ScenarioOutcome outcome = await fixture.Runner.RunAsync(scenarioId);

        Assert.True(outcome.Passed, outcome.Report());
    }
}
