using PromptHarness.Infrastructure.Engine;
using PromptHarness.Infrastructure.Wiring;
using Xunit;

namespace PromptHarness.Tests;

/// <summary>
/// The behavioural group: how a turn presents itself once it has decided what to do — whether it
/// stays in service, what buttons it shows, how it renders structured data.
/// </summary>
public sealed class BehaviourTests
{
    /// <summary>The live host, shared with every other test class in the assembly.</summary>
    private readonly MorganaHostFixture fixture;

    public BehaviourTests(MorganaHostFixture fixture) => this.fixture = fixture;

    [Theory]
    [InlineData("behaviour-turn-continuation-operand")]
    [InlineData("behaviour-conversation-closure")]
    [InlineData("behaviour-rich-card")]
    public async Task Behavioural_scenario_holds(string scenarioId)
    {
        // Same shape as the context-handling group's test, but this group runs at the harness's
        // default threshold rather than a mandatory 5/5 — see the class remarks for why that split
        // is deliberate rather than an oversight.
        ScenarioOutcome outcome = await fixture.Runner.RunAsync(scenarioId);

        Assert.True(outcome.Passed, outcome.Report());
    }
}
