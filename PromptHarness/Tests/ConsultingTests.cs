using PromptHarness.Infrastructure.Engine;
using PromptHarness.Infrastructure.Wiring;
using Xunit;

namespace PromptHarness.Tests;

/// <summary>
/// The group covering one agent consulting another: a colleague reached on demand for the right reason and an exchange that leaves the conversation as it found it.
/// It blocks like context handling because the failure is silent; a red here can also mean a broken topology, so the attributes are checked first.
/// The scenarios assert the mechanism without a judge, since a judge handed a broad prohibition goes hunting and finds something to convict.
/// </summary>
public sealed class ConsultingTests
{
    /// <summary>The live host, shared with every other test class in the assembly.</summary>
    private readonly MorganaHostFixture fixture;

    /// <summary>Receives the assembly's one live host.</summary>
    public ConsultingTests(MorganaHostFixture fixture) => this.fixture = fixture;

    /// <summary>Replays one consultation scenario and asserts it cleared its own pass threshold, 4/4 for this group.</summary>
    [Theory]
    [InlineData("consulting-fetches-the-missing-datum")]
    [InlineData("consulting-leaves-no-trace")]
    [InlineData("consulting-colleague-cannot-answer")]
    public async Task Consulting_scenario_holds(string scenarioId)
    {
        // The scenario's own runs and minPasses decide the threshold.
        ScenarioOutcome outcome = await fixture.Runner.RunAsync(scenarioId);

        // The full per-run transcript is the assertion message when the threshold was missed.
        Assert.True(outcome.Passed, outcome.Report());
    }
}