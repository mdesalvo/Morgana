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
        ScenarioOutcome outcome = await fixture.Runner.RunAsync(scenarioId);

        Assert.True(outcome.Passed, outcome.Report());
    }
}
