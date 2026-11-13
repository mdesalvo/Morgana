using Morgana.Contracts;
using PromptHarness.Infrastructure.Engine;
using PromptHarness.Infrastructure.Wiring;
using Xunit;

namespace PromptHarness.Tests;

/// <summary>
/// The framework prompts that the context and behaviour groups never exercise: the classifier's own output, the channel adapter's rewrite and the presentation's content.
/// </summary>
public sealed class ActorTests
{
    /// <summary>The live host, shared with every other test class in the assembly.</summary>
    private readonly MorganaHostFixture fixture;

    /// <summary>Receives the assembly's one live host.</summary>
    public ActorTests(MorganaHostFixture fixture) => this.fixture = fixture;

    [Theory]
    [InlineData("actor-classifier-routes-unambiguous-billing-request")]
    [InlineData("actor-classifier-routes-catalog-request-to-inventory")]
    [InlineData("actor-classifier-routes-care-request-to-gardening")]
    [InlineData("actor-complement-reintroduces-morgana")]
    [InlineData("actor-classifier-disambiguates-colliding-billing-contract")]
    [InlineData("actor-classifier-routes-off-domain-to-other")]
    [InlineData("actor-channeladapter-degrades-invoice-card")]
    public async Task Actor_scenario_holds(string scenarioId)
    {
        // The scenario's own runs and minPasses decide the threshold.
        ScenarioOutcome outcome = await fixture.Runner.RunAsync(scenarioId);

        // The full per-run transcript is the assertion message when the threshold was missed.
        Assert.True(outcome.Passed, outcome.Report());
    }

    /// <summary>
    /// Judges what the presentation says, where the smoke group only proves that one arrives.
    /// It is one shot and never a scenario of N runs: the presenter caches its result process-wide by channel name, so every later start replays the first.
    /// </summary>
    [Fact]
    public async Task Presentation_introduces_Morgana_and_invites_a_request()
    {
        // The welcome of a fresh conversation, which is the presenter's text or the one it cached for this channel.
        (string conversationId, ChannelMessage presentation) =
            await fixture.Channel.StartConversationAsync(TimeSpan.FromSeconds(fixture.Options.TurnTimeoutSeconds));

        try
        {
            // The judge reads the welcome as the user would and convicts on either proposition.
            IReadOnlyList<string> failures = await fixture.Judge.EvaluateAsync(
                judge: ["The message introduces itself as Morgana and invites the user to say what they need."],
                judgeNot: ["The message asks for personal or account information before the user has said anything."],
                message: presentation);

            Assert.True(failures.Count == 0, string.Join("\n", failures));
        }
        finally
        {
            // The conversation is ended whatever the verdict, so its actors do not outlive the test.
            await fixture.Channel.EndConversationAsync(conversationId);
        }
    }
}