using PromptHarness.Infrastructure.Engine;
using PromptHarness.Infrastructure.Wiring;
using Xunit;

namespace PromptHarness.Tests;

/// <summary>
/// The blocking group: the properties of context handling that no prompt revision may trade away.
/// </summary>
/// <remarks>
/// <para>The framework resolves context-scoped parameters, so what is left to the model is the
/// <strong>cycle</strong> (the tool is called before anything is asked, the user is asked only for
/// what it reports missing and a value the user gives is passed to the tool), the
/// <strong>hydration</strong> of a value another agent obtained and <strong>non-revelation</strong>
/// (the user never learns the context exists).</para>
/// </remarks>
public sealed class ContextHandlingTests
{
    /// <summary>The live host, shared with every other test class in the assembly.</summary>
    private readonly MorganaHostFixture fixture;

    public ContextHandlingTests(MorganaHostFixture fixture) => this.fixture = fixture;

    [Theory]
    [InlineData("context-cycle-on-miss")]
    [InlineData("context-cycle-on-hit")]
    [InlineData("context-cross-agent")]
    [InlineData("context-episode-return-same-agent")]
    public async Task ContextHandling_scenario_holds(string scenarioId)
    {
        // The scenario's own runs/minPasses (5/5 for this blocking group, by convention — see the
        // class remarks) decide the threshold; this test only asks whether the aggregate outcome
        // cleared it and prints the full per-run transcript on the assertion message when it didn't.
        ScenarioOutcome outcome = await fixture.Runner.RunAsync(scenarioId);

        Assert.True(outcome.Passed, outcome.Report());
    }
}
