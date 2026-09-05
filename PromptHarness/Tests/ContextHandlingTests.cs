using PromptHarness.Infrastructure.Engine;
using PromptHarness.Infrastructure.Wiring;
using Xunit;

namespace PromptHarness.Tests;

/// <summary>
/// The blocking group: the properties of context handling that no prompt revision may trade away.
/// </summary>
/// <remarks>
/// <para>Three properties are under test and they are the whole of context handling: the
/// <strong>cycle</strong> (an unheld value is read via <c>GetContextVariable</c> before asking, asked
/// only on a miss and written via <c>SetContextVariable</c> on the answer — while an already-held
/// value simply arrives in the per-turn <c>HeldContextDeclaration</c>, with no tool call needed at
/// all), the <strong>closed vocabulary</strong> (only declared context-scoped parameter names are
/// legal and none may be minted from the user's words) and <strong>non-revelation</strong> (the
/// user never learns the context exists).</para>
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
    [InlineData("context-closed-vocabulary-monkeys")]
    [InlineData("context-no-invented-writes")]
    public async Task Context_handling_scenario_holds(string scenarioId)
    {
        // The scenario's own runs/minPasses (5/5 for this blocking group, by convention — see the
        // class remarks) decide the threshold; this test only asks whether the aggregate outcome
        // cleared it and prints the full per-run transcript on the assertion message when it didn't.
        ScenarioOutcome outcome = await fixture.Runner.RunAsync(scenarioId);

        Assert.True(outcome.Passed, outcome.Report());
    }
}
