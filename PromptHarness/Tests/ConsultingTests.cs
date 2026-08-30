using PromptHarness.Infrastructure.Engine;
using PromptHarness.Infrastructure.Wiring;
using Xunit;

namespace PromptHarness.Tests;

/// <summary>
/// The group covering one agent consulting another: that a colleague is reached on demand and for
/// the right reason, and that the exchange leaves the conversation exactly as it found it.
/// </summary>
/// <remarks>
/// <para>Blocking like context handling, and for the same reason — the failure is silent: an agent
/// that stops consulting still answers, with less than it could have known. Its own class because
/// it also turns on a topology, so a failure here has a second thing it can mean: check the
/// attributes first.</para>
///
/// <para>Both scenarios assert the mechanism and nothing around it. Consultation carries a real
/// prohibition — the user never learns the exchange happened — but a judge handed a broad "must not
/// say" goes hunting through the response for something to convict, and finds it; what is worth
/// measuring here, and is measurable without a judge at all, is that the colleague was asked for a
/// datum only it holds and left no trace behind.</para>
/// </remarks>
public sealed class ConsultingTests
{
    /// <summary>The live host, shared with every other test class in the assembly.</summary>
    private readonly MorganaHostFixture fixture;

    public ConsultingTests(MorganaHostFixture fixture) => this.fixture = fixture;

    /// <summary>
    /// Replays one consultation scenario and asserts it cleared its own pass threshold, printing the
    /// full per-run transcript on the assertion message when it did not.
    /// </summary>
    [Theory]
    [InlineData("consulting-fetches-the-missing-datum")]
    [InlineData("consulting-leaves-no-trace")]
    [InlineData("consulting-colleague-cannot-answer")]
    public async Task Consulting_scenario_holds(string scenarioId)
    {
        ScenarioOutcome outcome = await fixture.Runner.RunAsync(scenarioId);

        Assert.True(outcome.Passed, outcome.Report());
    }
}
