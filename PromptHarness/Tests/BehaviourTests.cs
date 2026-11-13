using PromptHarness.Infrastructure.Engine;
using PromptHarness.Infrastructure.Wiring;
using Xunit;

namespace PromptHarness.Tests;

/// <summary>
/// The behavioural group: how a turn presents itself once it has decided what to do, such as whether it stays in service, what buttons it shows and how it renders structured data.
/// It runs at 5/4 rather than the blocking 5/5, since a presentation slip shows on the screen while a context slip does not.
/// </summary>
public sealed class BehaviourTests
{
    /// <summary>The live host, shared with every other test class in the assembly.</summary>
    private readonly MorganaHostFixture fixture;

    /// <summary>Receives the assembly's one live host.</summary>
    public BehaviourTests(MorganaHostFixture fixture) => this.fixture = fixture;

    [Theory]
    [InlineData("behaviour-turn-continuation-operand")]
    [InlineData("behaviour-landing-presents-the-agent")]
    [InlineData("behaviour-landing-only-on-a-press")]
    [InlineData("behaviour-conversation-closure")]
    [InlineData("behaviour-rich-card")]
    [InlineData("behaviour-order-confirmation-bills-invoice")]
    [InlineData("behaviour-order-cart-is-one-order")]
    [InlineData("behaviour-order-unknown-plant-creates-nothing")]
    [InlineData("behaviour-gardening-unknown-plant-gets-no-advice")]
    [InlineData("behaviour-gardening-diagnoses-wilting-roses")]
    [InlineData("behaviour-plan-enrollment-bills-invoice")]
    public async Task Behavioural_scenario_holds(string scenarioId)
    {
        // The scenario's own runs and minPasses decide the threshold, 5/4 for this group by convention.
        ScenarioOutcome outcome = await fixture.Runner.RunAsync(scenarioId);

        // The full per-run transcript is the assertion message when the threshold was missed.
        Assert.True(outcome.Passed, outcome.Report());
    }
}