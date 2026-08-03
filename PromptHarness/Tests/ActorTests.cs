using Morgana.Contracts;
using PromptHarness.Infrastructure.Engine;
using PromptHarness.Infrastructure.Wiring;
using Xunit;

namespace PromptHarness.Tests;

/// <summary>
/// The framework actors <c>ContextHandlingTests</c>/<c>BehaviourTests</c> never meaningfully
/// exercise: the classifier's own output, <c>MorganaChannelAdapter</c>'s degradation path, and the
/// Presentation prompt's actual content.
/// </summary>
public sealed class ActorTests
{
    /// <summary>The live host, shared with every other test class in the assembly.</summary>
    private readonly MorganaHostFixture fixture;

    public ActorTests(MorganaHostFixture fixture) => this.fixture = fixture;

    [Theory]
    [InlineData("classifier-routes-unambiguous-billing-request")]
    [InlineData("classifier-disambiguates-colliding-billing-contract")]
    [InlineData("channeladapter-degrades-invoice-card")]
    public async Task Actor_scenario_holds(string scenarioId)
    {
        ScenarioOutcome outcome = await fixture.Runner.RunAsync(scenarioId);

        Assert.True(outcome.Passed, outcome.Report());
    }

    /// <summary>
    /// Judges the Presentation prompt's actual content — distinct from
    /// <c>HarnessSmokeTests.Conversation_opens_and_Morgana_presents_itself</c>, which only proves
    /// the rig is alive (non-empty text, at least one quick reply) and leaves the wording unchecked.
    /// </summary>
    /// <remarks>
    /// Deliberately one-shot, never a 5-run YAML scenario: <c>LLMPresenterService</c> caches its
    /// result process-wide, keyed only by channel name — every conversation-start after the very
    /// first one in the whole assembly replays the cached result rather than calling the LLM again,
    /// so the standard N-runs/threshold method would be meaningless here. Do not "fix" this into a
    /// scenario later.
    /// </remarks>
    [Fact]
    public async Task Presentation_introduces_Morgana_and_invites_a_request()
    {
        (string conversationId, ChannelMessage presentation) =
            await fixture.Channel.StartConversationAsync(TimeSpan.FromSeconds(fixture.Options.TurnTimeoutSeconds));

        try
        {
            IReadOnlyList<string> failures = await fixture.Judge.EvaluateAsync(
                judge: ["The message introduces itself as Morgana and invites the user to say what they need."],
                judgeNot: ["The message asks for personal or account information before the user has said anything."],
                message: presentation);

            Assert.True(failures.Count == 0, string.Join("\n", failures));
        }
        finally
        {
            await fixture.Channel.EndConversationAsync(conversationId);
        }
    }
}