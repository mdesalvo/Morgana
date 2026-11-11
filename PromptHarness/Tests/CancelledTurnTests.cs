using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using A2A;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.A2A;
using Morgana.Contracts;
using PromptHarness.Infrastructure;
using PromptHarness.Infrastructure.Wiring;
using Xunit;

namespace PromptHarness.Tests;

/// <summary>
/// Every flow of a turn that Morgana can stop, held by a fake model until the wait on it runs out: the held call must be dropped in time, no late answer may reach the channel, a stopped turn keeps the tool calls that ran and only completed calls are charged.
/// </summary>
/// <remarks>
/// Requires <c>Harness__CancellableModel=true</c> in an invocation of its own, which points every tier at the fake model and sets the turn budget to six seconds; no real model is reached.
/// </remarks>
public sealed class CancelledTurnTests
{
    /// <summary>The live host, shared with every other test class in the assembly.</summary>
    private readonly MorganaHostFixture fixture;

    /// <summary>The API as a channel calls it, used here to read the records and to mint a partner's token.</summary>
    private readonly ChannelApiClient api;

    public CancelledTurnTests(MorganaHostFixture fixture)
    {
        this.fixture = fixture;
        api = new ChannelApiClient(fixture);
    }

    /// <summary>The turn budget that the fixture sets on a cancellation run, in seconds.</summary>
    private const double TurnSeconds = 6;

    /// <summary>The request that the inventory agent answers with its catalogue.</summary>
    private const string CatalogueRequest = "Show me the catalogue";

    /// <summary>What the inventory agent says once it has read the catalogue.</summary>
    private const string CatalogueAnswer = "Here is our catalogue.";

    /// <summary>A request that the classifier routes to the billing agent.</summary>
    private const string InvoiceRequest = "Show me my invoices";

    /// <summary>What the billing agent says once its colleague has answered or has not.</summary>
    private const string InvoiceAnswer = "Here is what I found.";

    /// <summary>The apology that the supervisor sends when the agent's turn runs out, spelled out as the user reads it.</summary>
    private const string TimeoutApology = "I apologize, time ran out before the cauldron could brew your answer. Cast it again.";

    /// <summary>The words of the tool result that tells the asking agent its colleague went silent, spelled out as the model reads them.</summary>
    private const string ColleagueSilent = "did not answer in time. Proceed without it.";

    /// <summary>The words that the held streamed answer of the inventory agent sent before it went silent.</summary>
    private const string StoppedStreamText = "Here is our";

    /// <summary>The words that the held streamed answer sends once its pause is over.</summary>
    private const string ResumedStreamText = " more";

    /// <summary>The tool that the inventory agent runs in its first round.</summary>
    private const string CatalogueTool = "GetProductCatalog";

    /// <summary>How long a channel waits for a delivery that is expected, which is far above the turn budget.</summary>
    private static readonly TimeSpan DeliveryWait = TimeSpan.FromSeconds(30);

    /// <summary>How long a client waits for a held call to be seen dropped after the delivery that proves it was.</summary>
    private static readonly TimeSpan DropWait = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task A_held_user_guard_is_dropped_and_the_message_is_refused_unjudged()
    {
        CancellableModelHost model = OpenModel();

        // The window opens before the conversation does, so that every call of the test is inside it.
        DateTimeOffset windowStart = DateTimeOffset.UtcNow;
        (string conversationId, ChannelMessage _) = await fixture.Channel.StartConversationAsync(DeliveryWait);

        // The next judgment of the user's message is the one that is held.
        model.HoldNext(ModelCallKind.UserGuard);
        ChannelMessage answer = await fixture.Channel.SendAsync(conversationId, CatalogueRequest, DeliveryWait);

        // A guard that went silent refuses the message in the UserGuard prompt's own wording.
        Assert.Equal(await NoVerdictMessageAsync(), answer.Text);
        await AssertHeldCallWasCancelledAsync(model, windowStart, ModelCallKind.UserGuard, 0, TurnSeconds);

        // Nothing past the guard runs: the message never reaches the classifier or an agent.
        Assert.Equal([(ModelCallKind.UserGuard, 0)], TurnSequence(model, windowStart));

        // No agent was reached, so no call of the inventory agent was made.
        Assert.DoesNotContain(CallsSince(model, windowStart), call => call.Kind == ModelCallKind.InventoryAgent);

        // Nor did the inventory agent ever leave a row on the record.
        Assert.Equal(0L, await api.QueryRecordAsync(conversationId, "select count(*) from morgana where agent_name = 'inventory'"));

        await AssertNoLateAnswerAsync(conversationId);
        await AssertDustIsSquareAsync(model, windowStart, conversationId);
        AssertNothingUnrecognised(model, windowStart);
    }

    [Fact]
    public async Task A_held_classifier_is_dropped_and_the_user_hears_the_classification_error()
    {
        CancellableModelHost model = OpenModel();

        // The window opens before the conversation does, so that every call of the test is inside it.
        DateTimeOffset windowStart = DateTimeOffset.UtcNow;
        (string conversationId, ChannelMessage _) = await fixture.Channel.StartConversationAsync(DeliveryWait);

        // The next classification is the one that is held.
        model.HoldNext(ModelCallKind.Classifier);
        ChannelMessage answer = await fixture.Channel.SendAsync(conversationId, CatalogueRequest, DeliveryWait);

        // A classifier that went silent is a technical failure, answered in the classifier prompt's own error wording.
        Assert.Equal(await ClassificationErrorAsync(), answer.Text);
        await AssertHeldCallWasCancelledAsync(model, windowStart, ModelCallKind.Classifier, 0, TurnSeconds);

        // No agent was reached, so no call of the inventory agent was made.
        Assert.DoesNotContain(CallsSince(model, windowStart), call => call.Kind == ModelCallKind.InventoryAgent);

        // Nor did the inventory agent ever leave a row on the record.
        Assert.Equal(0L, await api.QueryRecordAsync(conversationId, "select count(*) from morgana where agent_name = 'inventory'"));

        await AssertNoLateAnswerAsync(conversationId);
        await AssertDustIsSquareAsync(model, windowStart, conversationId);
        AssertNothingUnrecognised(model, windowStart);
    }

    [Fact]
    public async Task An_agent_turn_stopped_mid_stream_keeps_the_tool_calls_that_ran_and_drops_the_words_it_was_saying()
    {
        CancellableModelHost model = OpenModel();

        // The window opens before the conversation does, so that every call of the test is inside it.
        DateTimeOffset windowStart = DateTimeOffset.UtcNow;
        (string conversationId, ChannelMessage _) = await fixture.Channel.StartConversationAsync(DeliveryWait);

        // The second round of the agent is the one that is held, after three of its four words have been sent.
        model.HoldNext(ModelCallKind.InventoryAgent, round: 1);
        ChannelMessage apology = await fixture.Channel.SendAsync(conversationId, CatalogueRequest, DeliveryWait);

        // The instant the turn closed, which the held stream's late line must not precede.
        DateTimeOffset apologyAt = DateTimeOffset.UtcNow;

        // The supervisor tells the user that the turn ran out and the held call is dropped.
        // A stream that stays silent forever after its headers cannot be interrupted through OllamaSharp 5.5.2 and is out of this test.
        Assert.Equal(TimeoutApology, apology.Text);
        await AssertResumedStreamWasDroppedAsync(model, windowStart, ModelCallKind.InventoryAgent, 1, apologyAt);

        // A stopped turn leaves its agent's row inactive, so that the next message is classified afresh.
        Assert.Equal(0L, await api.QueryRecordAsync(conversationId, "select is_active from morgana where agent_name = 'inventory'"));

        // The next turn shows what the stopped one left in the agent's session.
        ChannelMessage followUp = await fixture.Channel.SendAsync(conversationId, "Anything else?", DeliveryWait);
        Assert.Equal(CatalogueAnswer, followUp.Text);
        ModelCallRecord followUpRequest = CallsSince(model, windowStart).Last(call => call.Kind == ModelCallKind.InventoryAgent && call.Round == 0);

        // The catalogue was read and its result came back, so both stay.
        Assert.Equal(1, CountToolCalls(followUpRequest.RequestJson, CatalogueTool));
        Assert.Equal(1, CountToolResults(followUpRequest.RequestJson));

        // The words of the stopped round were never said to the user, so they are not in the session.
        Assert.DoesNotContain(StoppedStreamText, MessageTexts(followUpRequest.RequestJson), StringComparison.Ordinal);

        // The line that the stream sent after the stop was never read into the session either.
        Assert.DoesNotContain(ResumedStreamText, MessageTexts(followUpRequest.RequestJson), StringComparison.Ordinal);

        await AssertNoLateAnswerAsync(conversationId);
        await AssertDustIsSquareAsync(model, windowStart, conversationId);
        AssertNothingUnrecognised(model, windowStart);
    }

    [Fact]
    public async Task A_held_tool_guard_stops_the_turn_and_the_call_that_never_got_its_result_is_dropped()
    {
        CancellableModelHost model = OpenModel();

        // The window opens before the conversation does, so that every call of the test is inside it.
        DateTimeOffset windowStart = DateTimeOffset.UtcNow;
        (string conversationId, ChannelMessage _) = await fixture.Channel.StartConversationAsync(DeliveryWait);

        // The inspection of the catalogue's result is the call that is held.
        model.HoldNext(ModelCallKind.ToolGuard);
        ChannelMessage apology = await fixture.Channel.SendAsync(conversationId, CatalogueRequest, DeliveryWait);

        // The turn is stopped inside the tool loop, so the user hears the same apology.
        Assert.Equal(TimeoutApology, apology.Text);
        await AssertHeldCallWasCancelledAsync(model, windowStart, ModelCallKind.ToolGuard, 0, TurnSeconds);

        // The next turn shows what the stopped one left in the agent's session.
        ChannelMessage followUp = await fixture.Channel.SendAsync(conversationId, "Anything else?", DeliveryWait);
        Assert.Equal(CatalogueAnswer, followUp.Text);
        ModelCallRecord followUpRequest = CallsSince(model, windowStart).Last(call => call.Kind == ModelCallKind.InventoryAgent && call.Round == 0);

        // The call whose result was never admitted is gone with its result.
        Assert.Equal(0, CountToolCalls(followUpRequest.RequestJson, CatalogueTool));
        Assert.Equal(0, CountToolResults(followUpRequest.RequestJson));

        await AssertNoLateAnswerAsync(conversationId);
        await AssertDustIsSquareAsync(model, windowStart, conversationId);
        AssertNothingUnrecognised(model, windowStart);
    }

    [Fact]
    public async Task A_partner_that_stops_waiting_drops_the_peer_guard_call_that_judges_its_question()
    {
        CancellableModelHost model = OpenModel();
        DateTimeOffset windowStart = DateTimeOffset.UtcNow;

        // The partner's own client gives up after three seconds, well inside the turn budget.
        TimeSpan partnerTimeout = TimeSpan.FromSeconds(3);

        // The judgment of the partner's question is the call that is held.
        model.HoldNext(ModelCallKind.PeerGuard);

        // The partner never gets an answer, so its own timeout ends the request.
        await Assert.ThrowsAnyAsync<Exception>(() => AskAsPartnerAsync(partnerTimeout, "Which plants are in stock right now?"));

        // The held call is dropped when the partner stops waiting, or the cancellation never crossed the door.
        ModelCallRecord held = await WaitForHeldCallAsync(model, windowStart, ModelCallKind.PeerGuard, 0);
        Assert.True(held.AbortedAt is not null, "The partner's cancellation did not reach the PeerGuard call.");
        AssertWithin(held.AbortedAt!.Value - (held.LastByteAt ?? held.StartedAt), partnerTimeout.TotalSeconds, "PeerGuard call");

        // A question that was never judged never reaches the agent.
        Assert.DoesNotContain(CallsSince(model, windowStart), call => call.Kind == ModelCallKind.ConsultedInventory);
        AssertNothingUnrecognised(model, windowStart);
    }

    [Fact]
    public async Task A_consultation_that_outlasts_the_colleagues_wait_is_dropped_and_the_asking_agent_goes_on_without_it()
    {
        CancellableModelHost model = OpenModel();

        // The window opens before the conversation does, so that every call of the test is inside it.
        DateTimeOffset windowStart = DateTimeOffset.UtcNow;
        (string conversationId, ChannelMessage _) = await fixture.Channel.StartConversationAsync(DeliveryWait);

        // The colleague's answer is the call that is held.
        model.HoldNext(ModelCallKind.ConsultedInventory);
        ChannelMessage answer = await fixture.Channel.SendAsync(conversationId, InvoiceRequest, DeliveryWait);

        // The billing agent finishes its turn without the colleague.
        Assert.Equal(InvoiceAnswer, answer.Text);

        // The colleague gives up first, at its share of the turn.
        await AssertHeldCallWasCancelledAsync(model, windowStart, ModelCallKind.ConsultedInventory, 0, TurnSeconds * 0.8);

        // The asking agent was told that the colleague went silent.
        ModelCallRecord billingRequest = CallsSince(model, windowStart).Last(call => call.Kind == ModelCallKind.BillingAgent && call.Round == 1);
        Assert.Contains(ColleagueSilent, ToolResultTexts(billingRequest.RequestJson), StringComparison.Ordinal);

        await AssertNoLateAnswerAsync(conversationId);
        await AssertDustIsSquareAsync(model, windowStart, conversationId);
        AssertNothingUnrecognised(model, windowStart);
    }

    /// <summary>The fake model of the run, or a skip of the test on a host booted without it.</summary>
    private CancellableModelHost OpenModel()
    {
        Assert.SkipWhen(!fixture.Options.CancellableModel,
            "Every tier answers from a real model: run with Harness__CancellableModel=true, in this class's own invocation.");
        return fixture.CancellableModel!;
    }

    /// <summary>The calls of the test's window, which opens before its conversation is started.</summary>
    private static List<ModelCallRecord> CallsSince(CancellableModelHost model, DateTimeOffset windowStart) =>
        model.Calls.Where(call => call.StartedAt >= windowStart).ToList();

    /// <summary>The flows of the window in the order they arrived, with the presentation left out because a process presents itself once.</summary>
    private static List<(ModelCallKind Kind, int Round)> TurnSequence(CancellableModelHost model, DateTimeOffset windowStart) =>
        CallsSince(model, windowStart)
            .Where(call => call.Kind != ModelCallKind.Presentation)
            .Select(call => (call.Kind, call.Round))
            .ToList();

    /// <summary>Finds the held call of that kind and round, waiting for the fake model to see its caller drop it.</summary>
    private static async Task<ModelCallRecord> WaitForHeldCallAsync(CancellableModelHost model, DateTimeOffset windowStart, ModelCallKind kind, int round)
    {
        // The caller's drop reaches the fake model a moment after the delivery that proves it, so it is waited for.
        DateTimeOffset deadline = DateTimeOffset.UtcNow + DropWait;
        while (true)
        {
            ModelCallRecord? held = CallsSince(model, windowStart)
                .FirstOrDefault(call => call.Kind == kind && call.Round == round && call.Held);

            // The call is returned once dropped or once the wait for the drop is over.
            if (held is not null && (held.AbortedAt is not null || DateTimeOffset.UtcNow > deadline))
                return held;

            // A held call that never arrived is a fault of the test and not of the drop.
            Assert.True(held is not null || DateTimeOffset.UtcNow <= deadline, $"No held {kind} call of round {round} reached the fake model.");
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Asserts that Morgana dropped the held call within a second before and three seconds after the wait that was meant to end it.</summary>
    private async Task AssertHeldCallWasCancelledAsync(CancellableModelHost model, DateTimeOffset windowStart, ModelCallKind kind, int round, double waitSeconds)
    {
        ModelCallRecord held = await WaitForHeldCallAsync(model, windowStart, kind, round);

        // A call that nobody dropped is a cancellation that never reached the model's HTTP call.
        Assert.True(held.AbortedAt is not null, $"The held {kind} call of round {round} was never dropped by Morgana (streamed {held.Streamed}, started {held.StartedAt:O}, last byte {held.LastByteAt:O}, now {DateTimeOffset.UtcNow:O}).");

        // The wait is renewed by every frame, so it counts from the last byte or from the start when none was sent.
        AssertWithin(held.AbortedAt!.Value - (held.LastByteAt ?? held.StartedAt), waitSeconds, $"{kind} call of round {round}");
    }

    /// <summary>Asserts that a held stream resumed after the turn closed and was dropped at the first line that followed.</summary>
    private async Task AssertResumedStreamWasDroppedAsync(CancellableModelHost model, DateTimeOffset windowStart, ModelCallKind kind, int round, DateTimeOffset apologyAt)
    {
        // The wait for the drop covers the pause of the stream and the line after it, which come after the turn budget.
        DateTimeOffset deadline = DateTimeOffset.UtcNow + DropWait + fixture.CancellableModel!.HeldStreamResumesAfter;
        ModelCallRecord held = await WaitForHeldCallAsync(model, windowStart, kind, round);
        while (held.AbortedAt is null && DateTimeOffset.UtcNow < deadline)
        {
            // The pause is still running or the drop has not been seen yet.
            await Task.Delay(100, TestContext.Current.CancellationToken);
            held = await WaitForHeldCallAsync(model, windowStart, kind, round);
        }

        // A stream that never resumed proves nothing about the reader.
        Assert.True(held.ResumedAt is not null, $"The held {kind} call of round {round} never resumed (started {held.StartedAt:O}, now {DateTimeOffset.UtcNow:O}).");

        // The turn closed on the budget and not on the late line.
        Assert.True(apologyAt <= held.ResumedAt!.Value, $"The turn closed at {apologyAt:O}, after the stream resumed at {held.ResumedAt:O}.");

        // A call that nobody dropped is a reader that kept waiting on the network.
        Assert.True(held.AbortedAt is not null, $"The held {kind} call of round {round} was never dropped by Morgana (started {held.StartedAt:O}, resumed {held.ResumedAt:O}, last byte {held.LastByteAt:O}, now {DateTimeOffset.UtcNow:O}).");

        // The reader stops at the first line after the stop and the HTTP client drains for up to two seconds before closing.
        TimeSpan sinceResume = held.AbortedAt!.Value - held.ResumedAt!.Value;
        Assert.True(
            sinceResume >= TimeSpan.Zero && sinceResume <= TimeSpan.FromSeconds(3),
            $"The {kind} call of round {round} resumed at {held.ResumedAt:O} and was dropped at {held.AbortedAt:O}, {sinceResume.TotalSeconds:F2} s later and outside [0, 3] s.");
    }

    /// <summary>Asserts that an interval lies within [wait - 1 s, wait + 3 s].</summary>
    private static void AssertWithin(TimeSpan elapsed, double waitSeconds, string description) =>
        Assert.True(
            elapsed.TotalSeconds >= waitSeconds - 1 && elapsed.TotalSeconds <= waitSeconds + 3,
            $"The {description} was dropped {elapsed.TotalSeconds:F2} s after its last byte, outside [{waitSeconds - 1:F1}, {waitSeconds + 3:F1}] s.");

    /// <summary>Asserts that nothing reaches the channel for twice the turn budget after the delivery the test expected.</summary>
    private async Task AssertNoLateAnswerAsync(string conversationId)
    {
        // A late answer of the stopped work would arrive within the window and be read as the reply to the next message.
        TimeoutException silence = await Assert.ThrowsAsync<TimeoutException>(
            () => fixture.Channel.ReceiveAsync(conversationId, TimeSpan.FromSeconds(TurnSeconds * 2)));
        Assert.NotNull(silence);
    }

    /// <summary>Asserts that the conversation was charged once for each completed call of the window and the same dust for each.</summary>
    private async Task AssertDustIsSquareAsync(CancellableModelHost model, DateTimeOffset windowStart, string conversationId)
    {
        // The presentation is charged to a conversation of its own; a call that was dropped is charged to none.
        int completedCalls = CallsSince(model, windowStart)
            .Count(call => call.Completed && call.Kind != ModelCallKind.Presentation);
        long chargedRows = Convert.ToInt64(await api.QueryRecordAsync(conversationId, "select count(*) from dust_usage_log"));
        Assert.Equal(completedCalls, chargedRows);

        // Every completed call reports the same token counts, so every row carries the same dust: one amount, none when nothing completed.
        long distinctAmounts = Convert.ToInt64(await api.QueryRecordAsync(conversationId, "select count(distinct dust_consumed) from dust_usage_log"));
        Assert.Equal(chargedRows == 0 ? 0L : 1L, distinctAmounts);
    }

    /// <summary>Asserts that no request of the window matched a flow of Morgana.</summary>
    private static void AssertNothingUnrecognised(CancellableModelHost model, DateTimeOffset windowStart) =>
        Assert.DoesNotContain(CallsSince(model, windowStart), call => call.Kind == ModelCallKind.Unrecognised);

    /// <summary>The wording of the Classifier prompt's ClassificationError in the morgana.json that ships with the framework.</summary>
    private static async Task<string> ClassificationErrorAsync()
    {
        // The framework prompts resolve over a domain that declares no agent, as the guards resolve them.
        Morgana.AI.Services.ConfigurationPromptResolverService resolver = new Morgana.AI.Services.ConfigurationPromptResolverService(new ScriptedDomain());
        Morgana.AI.Records.Prompt classifier = await resolver.ResolveAsync("Classifier");
        return classifier.GetAdditionalProperty<string>("ClassificationError");
    }

    /// <summary>The wording of the UserGuard prompt's NoVerdictMessage in the morgana.json that ships with the framework.</summary>
    private static async Task<string> NoVerdictMessageAsync()
    {
        // The framework prompts resolve over a domain that declares no agent, as the guards resolve them.
        Morgana.AI.Services.ConfigurationPromptResolverService resolver = new Morgana.AI.Services.ConfigurationPromptResolverService(new ScriptedDomain());
        Morgana.AI.Records.Prompt userGuard = await resolver.ResolveAsync("UserGuard");
        return userGuard.GetAdditionalProperty<string>("NoVerdictMessage");
    }

    /// <summary>Consults the inventory agent as the scoped partner does, on a client that gives up after the timeout.</summary>
    private async Task AskAsPartnerAsync(TimeSpan clientTimeout, string question)
    {
        using HttpClient httpClient = new HttpClient { Timeout = clientTimeout };
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", api.MintToken(MorganaHostFixture.ScopedPartnerName, fixture.ScopedPartnerKey));

        // The card is read first and the client is bound to the interface it advertises, as a partner learns where to knock.
        AgentCard card = await new A2ACardResolver(
            new Uri($"{fixture.BaseAddress}/a2a/{MorganaHostFixture.ScopedPartnerAgent}/"), httpClient)
            .GetAgentCardAsync(TestContext.Current.CancellationToken);
        AIAgent colleague = card.AsAIAgent(httpClient);

        // The conversation is named by the partner and served under the issuer the gate proved.
        AgentSession session = colleague is A2AAgent a2aColleague
            ? await a2aColleague.CreateSessionAsync($"cancelled-{Guid.NewGuid():N}")
            : await colleague.CreateSessionAsync(TestContext.Current.CancellationToken);

        // The caller declares itself an agent of a Morgana, as a partner's own Morgana does.
        AgentRunOptions options = new AgentRunOptions { AdditionalProperties = new() { ["morgana:caller"] = "billing" } };
        await colleague.RunAsync(question, session, options, TestContext.Current.CancellationToken);
    }

    /// <summary>The tool calls of that name that the request's messages hold.</summary>
    private static int CountToolCalls(string requestJson, string toolName) =>
        Messages(requestJson)
            .SelectMany(message => message?["tool_calls"] as JsonArray ?? [])
            .Count(toolCall => toolCall?["function"]?["name"]?.GetValue<string>() == toolName);

    /// <summary>The tool results that the request's messages hold.</summary>
    private static int CountToolResults(string requestJson) =>
        Messages(requestJson).Count(message => message?["role"]?.GetValue<string>() == "tool");

    /// <summary>The text of every tool result that the request's messages hold.</summary>
    private static string ToolResultTexts(string requestJson) =>
        string.Concat(Messages(requestJson)
            .Where(message => message?["role"]?.GetValue<string>() == "tool")
            .Select(message => message?["content"]?.GetValue<string>()));

    /// <summary>The text of every message the request carries, the system prompt left out.</summary>
    private static string MessageTexts(string requestJson) =>
        string.Concat(Messages(requestJson)
            .Where(message => message?["role"]?.GetValue<string>() != "system")
            .Select(message => message?["content"]?.GetValue<string>()));

    /// <summary>The messages array of a request.</summary>
    private static List<JsonNode?> Messages(string requestJson) =>
        (JsonNode.Parse(requestJson)?["messages"] as JsonArray ?? []).ToList();
}
