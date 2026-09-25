using Microsoft.Extensions.AI;
using Morgana.AI.Services;
using Morgana.Contracts;
using PromptHarness.Infrastructure.Engine;
using PromptHarness.Infrastructure.Wiring;
using Xunit;

namespace PromptHarness.Tests;

/// <summary>
/// The summarization prompt, which no other scenario has ever exercised: the default trigger (21
/// non-system messages — <c>SummarizationTargetCount</c> 8 + <c>SummarizationThreshold</c> 12) sits
/// far above what any scripted conversation reaches.
/// </summary>
/// <remarks>
/// <para><strong>Requires a lowered trigger at boot</strong> — like the guard rail, the reducer's
/// configuration is process-wide for the single assembly-shared host, so lowering it would silently
/// change every other class's few-turn conversations too. Run this class on its own:</para>
/// <code>Harness__SummarizationThreshold=4 Harness__SummarizationTargetCount=4 dotnet test PromptHarness.csproj --filter "FullyQualifiedName~SummarizationTests"</code>
///
/// <para>At 4+4 the trigger is 8 messages — comfortably below the ~16 the scripted scenario's first
/// two turns accumulate, so the reduction fires at the start of the third turn, after the material
/// worth compressing (and worth losing, if the prompt fails) already exists.</para>
/// </remarks>
public sealed class SummarizationTests
{
    /// <summary>The live host, shared with every other test class in the assembly.</summary>
    private readonly MorganaHostFixture fixture;

    public SummarizationTests(MorganaHostFixture fixture) => this.fixture = fixture;

    [Theory]
    [InlineData("summarization-preserves-invoice-details")]
    public async Task Summarization_scenario_holds(string scenarioId)
    {
        ScenarioOutcome outcome = await fixture.Runner.RunAsync(scenarioId);

        Assert.True(outcome.Passed, outcome.Report());
    }

    /// <summary>
    /// A /compact the channel stopped waiting on writes nothing and tells nothing: the channel already told the
    /// user it was called off. Two real turns give billing a history worth folding, then the call is dropped while
    /// the summary is being composed. The agent's row is never rewritten and no outcome is delivered after it.
    /// </summary>
    [Fact]
    public async Task Compact_the_channel_gave_up_on_writes_and_tells_nothing()
    {
        ChannelApiClient api = new ChannelApiClient(fixture);
        (string conversationId, ChannelMessage _) = await fixture.Channel.StartConversationAsync(TimeSpan.FromSeconds(180));
        await fixture.Channel.SendAsync(conversationId, "Vorrei vedere le mie fatture, il mio codice cliente è P994E", TimeSpan.FromSeconds(180));
        await fixture.Channel.SendAsync(conversationId, "Quali risultano ancora da pagare?", TimeSpan.FromSeconds(180));

        // Half a second reaches the summarization call, which no model answers that fast: the channel gives up
        // on a command that is working, which is the case a deadline exists for
        int logMark = fixture.Observer.Mark();
        using CancellationTokenSource channelDeadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            api.SendCommandAsync(conversationId, """{"name":"compact"}""", channelDeadline.Token));

        // Morgana notices the dropped call and stops, saying so in its log alone
        DateTime giveUpAt = DateTime.UtcNow.AddSeconds(60);
        while (!fixture.Output.Since(logMark).Any(line => line.Contains("was abandoned by the channel")) && DateTime.UtcNow < giveUpAt)
            await Task.Delay(250);
        IReadOnlyList<string> log = fixture.Output.Since(logMark);
        Assert.True(log.Any(line => line.Contains("was abandoned by the channel")), "Morgana never noticed the channel had stopped waiting on /compact.");
        Assert.False(log.Any(line => line.Contains("Rewrote the")), "/compact rewrote the agent's history after the channel had stopped waiting on it.");
        Assert.Equal(0L, await api.QueryRecordAsync(conversationId, "SELECT is_dirty FROM morgana WHERE agent_name = 'billing';"));

        // Frames sent before the call was dropped may still land; an outcome never does, since it would contradict
        // what the channel already told the user
        List<ChannelMessage> delivered = [];
        try
        {
            while (true)
                delivered.Add(await fixture.Channel.ReceiveAsync(conversationId, TimeSpan.FromSeconds(5)));
        }
        catch (TimeoutException)
        {
            // Nothing more arrived: every delivery the command made is in the list
        }
        Assert.DoesNotContain(delivered, message => message.Progress is { Finished: true });
    }

    /// <summary>
    /// A fold written while the agent's turn is running survives the save that closes the turn: the agent appends
    /// what it said on top of the fold instead of writing back the history the fold replaced. The fold here is
    /// written straight to the record, the way a command reaching the conversation over REST would land it.
    /// </summary>
    [Fact]
    public async Task Fold_written_during_a_turn_survives_the_turn()
    {
        SQLiteConversationPersistenceService record = new ChannelApiClient(fixture).HostPersistenceService();
        (string conversationId, ChannelMessage _) = await fixture.Channel.StartConversationAsync(TimeSpan.FromSeconds(180));
        await fixture.Channel.SendAsync(conversationId, "Vorrei vedere le mie fatture, il mio codice cliente è P994E", TimeSpan.FromSeconds(180));
        int recordedBeforeTurn = (await record.LoadParticipantMessagesAsync(conversationId, "billing")).Count;

        Task<ChannelMessage> reply = fixture.Channel.SendAsync(conversationId, "Quali risultano ancora da pagare?", TimeSpan.FromSeconds(180));

        // The agent files the user's phrase before its model answers, which is the moment the turn is under way
        IReadOnlyList<ChatMessage> recordedInTurn = [];
        DateTime giveUpAt = DateTime.UtcNow.AddSeconds(60);
        while (!reply.IsCompleted && DateTime.UtcNow < giveUpAt
               && (recordedInTurn = await record.LoadParticipantMessagesAsync(conversationId, "billing")).Count <= recordedBeforeTurn)
            await Task.Delay(50);
        if (reply.IsCompleted)
            Assert.Skip("The turn ended before a fold could land inside it, so nothing was proven.");

        ChatMessage fold = new ChatMessage(ChatRole.Assistant, "Harness fold of everything said so far.");
        Assert.True(await record.SaveParticipantMessagesAsync(conversationId, "billing", [fold], recordedInTurn.Count));
        await reply;

        // The fold opens the row and the turn's answer follows it; the agent reads it back at its next turn
        IReadOnlyList<ChatMessage> afterTurn = await record.LoadParticipantMessagesAsync(conversationId, "billing");
        Assert.Equal(fold.Text, afterTurn[0].Text);
        Assert.Equal(ChatRole.Assistant, afterTurn[^1].Role);
        Assert.True(afterTurn.Count > 1, "The turn that ran over the fold left nothing of its own on record.");
        Assert.Equal(1L, await new ChannelApiClient(fixture).QueryRecordAsync(conversationId, "SELECT is_dirty FROM morgana WHERE agent_name = 'billing';"));

        await fixture.Channel.SendAsync(conversationId, "Grazie, per ora è tutto", TimeSpan.FromSeconds(180));
        Assert.Equal(fold.Text, (await record.LoadParticipantMessagesAsync(conversationId, "billing"))[0].Text);
        Assert.Equal(0L, await new ChannelApiClient(fixture).QueryRecordAsync(conversationId, "SELECT is_dirty FROM morgana WHERE agent_name = 'billing';"));
    }
}
