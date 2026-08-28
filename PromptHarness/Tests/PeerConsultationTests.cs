using PromptHarness.Infrastructure.Engine;
using PromptHarness.Infrastructure.Wiring;
using Xunit;

namespace PromptHarness.Tests;

/// <summary>
/// The group covering one agent consulting another: that a colleague is reached when only the
/// colleague can answer, and that the user never learns it happened.
/// </summary>
/// <remarks>
/// Blocking like context handling, and for the same reason — the failure is silent: an agent that
/// stops consulting still answers, with less than it could have known. Its own class because it also
/// turns on a topology, so a failure here has a second thing it can mean: check the attributes first.
/// </remarks>
public sealed class PeerConsultationTests
{
    /// <summary>The live host, shared with every other test class in the assembly.</summary>
    private readonly MorganaHostFixture fixture;

    public PeerConsultationTests(MorganaHostFixture fixture) => this.fixture = fixture;

    /// <summary>
    /// Replays one consultation scenario and asserts it cleared its own pass threshold, printing the
    /// full per-run transcript on the assertion message when it did not.
    /// </summary>
    [Theory]
    [InlineData("peer-consultation-non-revelation")]
    public async Task Peer_consultation_scenario_holds(string scenarioId)
    {
        ScenarioOutcome outcome = await fixture.Runner.RunAsync(scenarioId);

        Assert.True(outcome.Passed, outcome.Report());
    }
}
