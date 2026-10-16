using Morgana.Contracts;
using PromptHarness.Infrastructure;
using PromptHarness.Infrastructure.Engine;
using PromptHarness.Infrastructure.Wiring;
using Xunit;

namespace PromptHarness.Tests;

/// <summary>
/// The framework actors <c>ContextHandlingTests</c>/<c>BehaviourTests</c> never meaningfully
/// exercise: the classifier's own output, <c>MorganaChannelAdapter</c>'s degradation path and the
/// Presentation prompt's actual content.
/// </summary>
public sealed class ActorTests
{
    /// <summary>The live host, shared with every other test class in the assembly.</summary>
    private readonly MorganaHostFixture fixture;

    public ActorTests(MorganaHostFixture fixture) => this.fixture = fixture;

    [Theory]
    [InlineData("actor-classifier-routes-unambiguous-billing-request")]
    [InlineData("actor-classifier-routes-catalog-request-to-inventory")]
    [InlineData("actor-classifier-disambiguates-colliding-billing-contract")]
    [InlineData("actor-classifier-routes-off-domain-to-other")]
    [InlineData("actor-channeladapter-degrades-invoice-card")]
    public async Task Actor_scenario_holds(string scenarioId)
    {
        ScenarioOutcome outcome = await fixture.Runner.RunAsync(scenarioId);

        Assert.True(outcome.Passed, outcome.Report());
    }

    /// <summary>
    /// Judges the Presentation prompt's actual content — distinct from
    /// <c>HarnessSmokeTests.HarnessSmoke_Conversation_opens_and_Morgana_presents_itself</c>, which only proves
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
    public async Task Actor_presentation_introduces_Morgana_and_invites_a_request()
    {
        (string conversationId, ChannelMessage presentation) =
            await fixture.Channel.StartConversationAsync(TimeSpan.FromSeconds(fixture.Options.TurnTimeoutSeconds));

        try
        {
            // The buttons are written by code from the configured intents, so they are asserted exactly and in configuration order.
            Assert.Equal(
                [
                    ("billing", "🧾 Invoices", "What can you do with my invoices?"),
                    ("contract", "📜 Care Plan", "What can you tell me about the care plan?"),
                    ("monkeys", "🐒 Primate Almanac", "What can you tell me about monkeys?"),
                    ("inventory", "🌿 Greenhouse", "What can you do for me in the greenhouse?")
                ],
                (presentation.QuickReplies ?? []).Select(button => (button.Id, button.Label, button.Value)));

            // The judge weighs only what the model wrote: an introduction and an invitation to press a button,
            // the one way a channel showing buttons lets the user answer it.
            IReadOnlyList<string> failures = await fixture.Judge.EvaluateAsync(
                judge:
                [
                    "The message introduces itself as Morgana.",
                    "The message invites the user to press one of the buttons beneath it."
                ],
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