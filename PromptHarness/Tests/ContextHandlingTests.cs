using PromptHarness.Infrastructure.Engine;
using PromptHarness.Infrastructure.Wiring;
using Xunit;

namespace PromptHarness.Tests;

/// <summary>
/// The blocking group: the properties of context handling that no prompt revision may trade away.
/// The cycle reads an unheld value before asking, asks only on a miss and writes the answer, while a held value arrives in the per-turn declaration.
/// The closed vocabulary admits only declared context-scoped names; non-revelation keeps the user from learning the context exists.
/// </summary>
public sealed class ContextHandlingTests
{
    /// <summary>The live host, shared with every other test class in the assembly.</summary>
    private readonly MorganaHostFixture fixture;

    /// <summary>Receives the assembly's one live host.</summary>
    public ContextHandlingTests(MorganaHostFixture fixture) => this.fixture = fixture;

    [Theory]
    [InlineData("context-cycle-on-miss")]
    [InlineData("context-cycle-on-hit")]
    [InlineData("context-cross-agent")]
    [InlineData("context-closed-vocabulary-gardening")]
    [InlineData("context-no-invented-writes")]
    public async Task Context_handling_scenario_holds(string scenarioId)
    {
        // The scenario's own runs and minPasses decide the threshold, 5/5 for this blocking group by convention.
        ScenarioOutcome outcome = await fixture.Runner.RunAsync(scenarioId);

        // The full per-run transcript is the assertion message when the threshold was missed.
        Assert.True(outcome.Passed, outcome.Report());
    }
}