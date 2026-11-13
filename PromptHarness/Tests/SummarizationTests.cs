using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Morgana.AI.Services;
using Morgana.Contracts;
using PromptHarness.Infrastructure.Engine;
using PromptHarness.Infrastructure.Wiring;
using Xunit;

namespace PromptHarness.Tests;

/// <summary>
/// The summarization prompt and <c>/compact</c>, which no other group reaches: the default trigger of 21 messages sits far above any scripted conversation.
/// It needs Harness__SummarizationThreshold=4 and Harness__SummarizationTargetCount=4 in an invocation of its own, since the reducer is process-wide.
/// At 4 and 4 the reduction fires at the start of the third turn, once the material worth keeping already exists.
/// </summary>
public sealed class SummarizationTests
{
    /// <summary>The live host, shared with every other test class in the assembly.</summary>
    private readonly MorganaHostFixture fixture;

    /// <summary>Receives the assembly's one live host.</summary>
    public SummarizationTests(MorganaHostFixture fixture) => this.fixture = fixture;

    [Theory]
    [InlineData("summarization-preserves-invoice-details")]
    public async Task Summarization_scenario_holds(string scenarioId)
    {
        // The scenario's own runs and minPasses decide the threshold.
        ScenarioOutcome outcome = await fixture.Runner.RunAsync(scenarioId);

        // The full per-run transcript is the assertion message when the threshold was missed.
        Assert.True(outcome.Passed, outcome.Report());
    }

    /// <summary>
    /// A /compact the channel stopped waiting on writes nothing and tells nothing, since the channel already told the user it was called off.
    /// Two real turns give billing a history worth folding, then the call is dropped while the summary is being composed.
    /// </summary>
    [Fact]
    public async Task Compact_the_channel_gave_up_on_writes_and_tells_nothing()
    {
        // Two real billing turns give the agent a history long enough to fold.
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
            await Task.Delay(250, TestContext.Current.CancellationToken);

        // The record is untouched: no rewrite logged and the row not marked to be read again.
        IReadOnlyList<string> log = fixture.Output.Since(logMark);
        Assert.True(log.Any(line => line.Contains("was abandoned by the channel")), "Morgana never noticed the channel had stopped waiting on /compact.");
        Assert.False(log.Any(line => line.Contains("Rewrote the")), "/compact rewrote the agent's history after the channel had stopped waiting on it.");
        Assert.Equal(0L, await api.QueryRecordAsync(conversationId, "SELECT is_dirty FROM morgana WHERE agent_name = 'billing';"));

        // Frames sent before the call was dropped may still land; an outcome never does, since it would contradict
        // what the channel already told the user
        List<ChannelMessage> delivered = [];
        try
        {
            // Everything still arriving, until five quiet seconds say the command is done delivering.
            while (true)
                delivered.Add(await fixture.Channel.ReceiveAsync(conversationId, TimeSpan.FromSeconds(5)));
        }
        catch (TimeoutException)
        {
            // Nothing more arrived: every delivery the command made is in the list
        }

        // No finished frame, which is the outcome.
        Assert.DoesNotContain(delivered, message => message.Progress is { Finished: true });
    }

    /// <summary>A /compact fold stays the agent's summary after the agent reads its rewritten row back, so its next turn summarizes nothing again.</summary>
    [Fact]
    public async Task Compact_fold_is_read_back_after_the_agent_reloads_its_row()
    {
        // The REST client that reaches the command endpoint the way a channel's palette does
        ChannelApiClient api = new ChannelApiClient(fixture);

        // Two billing turns give the agent a history long enough for a fold to have something to replace
        (string conversationId, ChannelMessage _) = await fixture.Channel.StartConversationAsync(TimeSpan.FromSeconds(180));
        await fixture.Channel.SendAsync(conversationId, "Vorrei vedere le mie fatture, il mio codice cliente è P994E", TimeSpan.FromSeconds(180));
        await fixture.Channel.SendAsync(conversationId, "Quali risultano ancora da pagare?", TimeSpan.FromSeconds(180));

        // The fold is stamped and written behind the agent, which marks its row to be read again
        using HttpResponseMessage compactResponse = await api.SendCommandAsync(conversationId, """{"name":"compact"}""", TestContext.Current.CancellationToken);
        compactResponse.EnsureSuccessStatusCode();

        // The command reports over the channel; its last frame carries the outcome
        ChannelMessage compactFrame;

        // The progress frames come first and the finished one closes the command.
        do
            compactFrame = await fixture.Channel.ReceiveAsync(conversationId, TimeSpan.FromSeconds(180));
        while (compactFrame.Progress is not { Finished: true });

        // Two billing turns always hold more than the kept window, so a fold that came to nothing is a rig fault
        Assert.StartsWith("Compacted ", compactFrame.Text, StringComparison.Ordinal);

        // Everything logged from here on belongs to the turn that reads the rewritten row back
        int reloadMark = fixture.Output.Mark();

        // The agent's next turn reads its row back, which is where the marker has to be recognised
        await fixture.Channel.SendAsync(conversationId, "E quella di marzo a quanto ammonta?", TimeSpan.FromSeconds(180));
        IReadOnlyList<string> reloadLog = fixture.Output.Since(reloadMark);

        // Without a reload the marker was never put to the test
        Assert.True(reloadLog.Any(line => line.Contains("Loaded existing conversation session", StringComparison.Ordinal)), "billing never read its rewritten row back, so nothing was proven.");

        // A recognised marker leaves nothing to summarize: the reducer stays silent
        Assert.False(reloadLog.Any(line => line.Contains("MorganaChatReducer summarizing", StringComparison.Ordinal)), "billing summarized its history again after the reload: the fold's marker was not recognised.");

        // The provider logs how many messages the model was handed out of how many the row holds
        // The counts of the first reduced view the reload produced; none at all fails below.
        Match reducedView = reloadLog
            .Select(line => Regex.Match(line, @"PROVIDING reduced view \((\d+) → (\d+) messages\)"))
            .FirstOrDefault(match => match.Success) ?? Match.Empty;
        Assert.True(reducedView.Success, "billing's history provider logged no reduced view for the turn after the reload.");

        // The fold's summary stands in for the history, so the model reads fewer messages than the row holds
        int messagesInRow = int.Parse(reducedView.Groups[1].Value, CultureInfo.InvariantCulture);
        int messagesForModel = int.Parse(reducedView.Groups[2].Value, CultureInfo.InvariantCulture);
        Assert.True(messagesForModel < messagesInRow, "billing's model read its whole history after the reload instead of the fold's summary.");
    }

    /// <summary>
    /// A fold written while the agent's turn is running survives the save that closes the turn: the agent appends to the fold instead of writing back what it replaced.
    /// The fold is written straight to the record, the way a command reaching the conversation over REST lands it.
    /// </summary>
    [Fact]
    public async Task Fold_written_during_a_turn_survives_the_turn()
    {
        // The host's own record and one billing turn already on it.
        SQLiteConversationPersistenceService record = new ChannelApiClient(fixture).HostPersistenceService();
        (string conversationId, ChannelMessage _) = await fixture.Channel.StartConversationAsync(TimeSpan.FromSeconds(180));
        await fixture.Channel.SendAsync(conversationId, "Vorrei vedere le mie fatture, il mio codice cliente è P994E", TimeSpan.FromSeconds(180));
        int recordedBeforeTurn = (await record.LoadParticipantMessagesAsync(conversationId, "billing")).Count;

        // The second turn starts without being awaited, so the fold can land while it runs.
        Task<ChannelMessage> reply = fixture.Channel.SendAsync(conversationId, "Quali risultano ancora da pagare?", TimeSpan.FromSeconds(180));

        // The agent files the user's phrase before its model answers, which is the moment the turn is under way
        IReadOnlyList<ChatMessage> recordedInTurn = [];
        DateTime giveUpAt = DateTime.UtcNow.AddSeconds(60);
        while (!reply.IsCompleted && DateTime.UtcNow < giveUpAt
               && (recordedInTurn = await record.LoadParticipantMessagesAsync(conversationId, "billing")).Count <= recordedBeforeTurn)
            await Task.Delay(50, TestContext.Current.CancellationToken);

        // A turn quicker than the poll leaves no moment to fold in, which proves nothing either way.
        if (reply.IsCompleted)
            Assert.Skip("The turn ended before a fold could land inside it, so nothing was proven.");

        // The fold replaces the whole row as /compact would, while the turn is still running.
        ChatMessage fold = new ChatMessage(ChatRole.Assistant, "Harness fold of everything said so far.");
        Assert.True(await record.SaveParticipantMessagesAsync(conversationId, "billing", [fold], recordedInTurn.Count));
        await reply;

        // The fold opens the row and the turn's answer follows it; the agent reads it back at its next turn
        IReadOnlyList<ChatMessage> afterTurn = await record.LoadParticipantMessagesAsync(conversationId, "billing");
        Assert.Equal(fold.Text, afterTurn[0].Text);
        Assert.Equal(ChatRole.Assistant, afterTurn[^1].Role);
        Assert.True(afterTurn.Count > 1, "The turn that ran over the fold left nothing of its own on record.");
        Assert.Equal(1L, await new ChannelApiClient(fixture).QueryRecordAsync(conversationId, "SELECT is_dirty FROM morgana WHERE agent_name = 'billing';"));

        // The next turn reads the row back: the fold still opens it and the mark to read it again is cleared.
        await fixture.Channel.SendAsync(conversationId, "Grazie, per ora è tutto", TimeSpan.FromSeconds(180));
        Assert.Equal(fold.Text, (await record.LoadParticipantMessagesAsync(conversationId, "billing"))[0].Text);
        Assert.Equal(0L, await new ChannelApiClient(fixture).QueryRecordAsync(conversationId, "SELECT is_dirty FROM morgana WHERE agent_name = 'billing';"));
    }
}