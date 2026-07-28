using PromptHarness.Infrastructure;
using PromptHarness.Scenarios;
using Xunit;

namespace PromptHarness;

/// <summary>
/// The behavioural group: how a turn presents itself once it has decided what to do — whether it
/// stays in service, what buttons it shows, how it renders structured data.
/// </summary>
/// <remarks>
/// These run at the default threshold rather than 5/5. Their failure mode is visible to a user the
/// moment it happens, and their subject matter — button emission, card rendering, closure — is
/// exactly the surface a prompt revision is allowed to move, as long as it does not break.
/// </remarks>
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
