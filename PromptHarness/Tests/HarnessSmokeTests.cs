using Morgana.Contracts;
using PromptHarness.Infrastructure.Engine;
using PromptHarness.Infrastructure.Wiring;
using Xunit;

namespace PromptHarness.Tests;

/// <summary>
/// Checks the wiring itself before any scenario is believed: host up, handshake accepted, webhook
/// delivered, span and log observers wired.
/// </summary>
public sealed class HarnessSmokeTests
{
    /// <summary>The live host, shared with every other test class in the assembly.</summary>
    private readonly MorganaHostFixture fixture;

    public HarnessSmokeTests(MorganaHostFixture fixture) => this.fixture = fixture;

    [Fact]
    public async Task Conversation_opens_and_Morgana_presents_itself()
    {
        (string conversationId, ChannelMessage presentation) =
            await fixture.Channel.StartConversationAsync(TimeSpan.FromSeconds(fixture.Options.TurnTimeoutSeconds));

        try
        {
            Assert.False(string.IsNullOrWhiteSpace(presentation.Text), "The presentation carried no text.");
            Assert.NotEmpty(presentation.QuickReplies ?? []);
        }
        finally
        {
            await fixture.Channel.EndConversationAsync(conversationId);
        }
    }

    [Fact]
    public async Task Structural_observers_see_the_turn()
    {
        TimeSpan timeout = TimeSpan.FromSeconds(fixture.Options.TurnTimeoutSeconds);
        (string conversationId, ChannelMessage _) = await fixture.Channel.StartConversationAsync(timeout);

        try
        {
            TurnScope scope = fixture.Observer.BeginTurn(conversationId);
            ChannelMessage message = await fixture.Channel.SendAsync(conversationId, "I'd like to see my invoices", timeout);
            TurnResult turn = await fixture.Observer.CompleteTurnAsync(scope, "I'd like to see my invoices", message);

            // The span observer: an agent handled the turn and its name reached the listener.
            Assert.False(string.IsNullOrWhiteSpace(turn.AgentName),
                $"No morgana.agent span was observed for the turn.\n{turn.Describe()}");

            // The log observer: a turn always produces host log lines. Asserting on the lines
            // rather than on parsed context accesses keeps this a test of the rig — an agent that
            // skipped GetContextVariable would be a prompt regression, and that verdict belongs to
            // the context-handling scenarios, not here.
            Assert.NotEmpty(turn.LogLines);
        }
        finally
        {
            await fixture.Channel.EndConversationAsync(conversationId);
        }
    }

    [Fact]
    public void Every_scenario_file_parses()
    {
        string[] files = Directory.GetFiles(ScenarioLoader.Directory, "*.yaml");
        Assert.NotEmpty(files);

        foreach (string file in files)
        {
            ScenarioDefinition scenario = ScenarioLoader.Load(Path.GetFileNameWithoutExtension(file));

            Assert.Equal(Path.GetFileNameWithoutExtension(file), scenario.Id);
            Assert.NotEmpty(scenario.Turns);
        }
    }
}
