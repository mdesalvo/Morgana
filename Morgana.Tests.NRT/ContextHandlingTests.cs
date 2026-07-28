using Morgana.Tests.NRT.Infrastructure;
using Morgana.Tests.NRT.Scenarios;
using Xunit;

namespace Morgana.Tests.NRT;

/// <summary>
/// The blocking group: the properties of <c>GetContextVariable</c> / <c>SetContextVariable</c> that
/// no prompt revision may trade away.
/// </summary>
/// <remarks>
/// <para>Three properties are under test, and they are the whole of context handling: the
/// <strong>cycle</strong> (read before asking, ask only on a miss, write on the answer), the
/// <strong>closed vocabulary</strong> (only declared context-scoped parameter names are legal, and
/// none may be minted from the user's words), and <strong>non-revelation</strong> (the user never
/// learns the context exists).</para>
///
/// <para>Every scenario here runs at a 5/5 threshold, unlike the behavioural group. A property that
/// holds four times out of five is not a property — and these are the ones whose failure mode is
/// silent: an agent that asks again for something it already knows still looks like it works.</para>
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
        ScenarioOutcome outcome = await fixture.Runner.RunAsync(scenarioId);

        Assert.True(outcome.Passed, outcome.Report());
    }
}
