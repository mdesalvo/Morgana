using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using PromptHarness.Infrastructure.Wiring;
using Morgana.Contracts;
using Xunit;

namespace PromptHarness.Tests;

/// <summary>
/// The Magic Dust budget mechanics, which no other scenario has ever exercised: the default budget
/// (100, calibrated for ~10 full-length Performance turns) sits far above what any scripted
/// conversation reaches and <c>Morgana:DustLimiting:Enabled</c> is force-disabled everywhere else.
/// </summary>
/// <remarks>
/// <para><strong>Requires dust limiting at boot</strong> — like the guard rail and the
/// summarization reducer, dust limiting is process-wide for the single assembly-shared host, so
/// enabling it would silently start throttling every other class's conversations too. Run this class
/// on its own. The budget only has to be at least twenty times one turn of the tier under test, which
/// the test checks and reports:</para>
/// <code>Harness__DustBudgetPerConversation=100 dotnet test PromptHarness.csproj --filter "FullyQualifiedName~DustTests"</code>
///
/// <para><strong>Calibrated at runtime.</strong> The host logs one dust line per turn and the higher
/// threshold wins when a turn crosses two, so a fixed budget would hold for one tier price only. The
/// test measures one real turn on the tier under test, places the consumption a quarter of that turn
/// below each threshold and lets one real turn cross it. A tier priced at zero has no budget to walk
/// and the test is skipped.</para>
/// </remarks>
public sealed class DustTests
{
    /// <summary>Seeded customer with real invoices to answer against — see BillingAgent's scenarios.</summary>
    private const string CustomerCode = "P994E";

    /// <summary>The live host, shared with every other test class in the assembly.</summary>
    private readonly MorganaHostFixture fixture;

    public DustTests(MorganaHostFixture fixture) => this.fixture = fixture;

    /// <summary>
    /// One real turn measures what a turn costs on the tier the agent runs on, then the consumption is placed
    /// just below each threshold in turn and one real turn crosses it, so each warning and the exhaustion are
    /// seen alone and in order, whatever provider serves the tier.
    /// </summary>
    [Fact]
    public async Task Dust_thresholds_fire_in_order()
    {
        // Without a boot budget the host never charges or logs dust, so there is nothing to observe
        Assert.SkipWhen(fixture.Options.DustBudgetPerConversation is null,
            "Dust limiting is off: run with Harness__DustBudgetPerConversation set, in this class's own invocation.");

        // The budget the host booted with, which every placement below is a fraction of
        double budget = fixture.Options.DustBudgetPerConversation!.Value;

        (string conversationId, ChannelMessage _) = await fixture.Channel.StartConversationAsync(TimeSpan.FromSeconds(180));

        // Taken once, before any turn, so the log window checked after each turn spans the whole
        // conversation so far — a threshold is one-shot and never resent, so a turn's OWN window can
        // miss it entirely depending on which turn happens to cross it.
        int conversationMark = fixture.Observer.Mark();

        // Reaches the conversation's own record, where the consumption is read and placed
        ChannelApiClient api = new ChannelApiClient(fixture);

        try
        {
            // The gauge before the measuring turn, so the turn's own charge is a difference and not the total
            double consumedBeforeMeasure = await ReadConsumedAsync(api, conversationId);

            // The one real turn whose charge calibrates every placement below
            string measuringSay = $"Hi, my customer code is {CustomerCode} — show me my last invoice";
            TurnScope measuringScope = fixture.Observer.BeginTurn(conversationId);
            ChannelMessage measuringMessage = await fixture.Channel.SendAsync(conversationId, measuringSay, TimeSpan.FromSeconds(180));
            await fixture.Observer.CompleteTurnAsync(measuringScope, measuringSay, measuringMessage, conversationMark);

            // What one turn costs on the tier under test, in the host's own dust
            double turnCost = await ReadConsumedAsync(api, conversationId) - consumedBeforeMeasure;

            // A free tier never moves the gauge, so no threshold can be crossed
            Assert.SkipWhen(turnCost <= 0, "The tier the agent runs on prices its model at zero: there is no budget to walk.");

            // Twenty turns of room keep the gauge far from the threshold above each placement
            Assert.True(turnCost * 20 <= budget, string.Create(CultureInfo.InvariantCulture,
                $"One turn costs {turnCost:F2} dust on the tier under test: a budget of {budget} leaves too little room between two thresholds. Run with Harness__DustBudgetPerConversation of at least {turnCost * 20:F0}."));

            // A quarter turn below 70%: the next turn crosses 70% alone
            TurnResult crossing70 = await CrossAsync(api, conversationId, conversationMark,
                budget * 0.70 - turnCost / 4, "And my outstanding balance?");
            Assert.True(crossing70.Cumulative.Any(line => line.Contains("DUST WARNING (70%)", StringComparison.Ordinal)),
                string.Create(CultureInfo.InvariantCulture, $"After placing the gauge a quarter turn below 70% (turn cost {turnCost:F2}, budget {budget}) the 70% warning did not appear."));
            Assert.False(crossing70.Cumulative.Any(line => line.Contains("DUST WARNING (90%)", StringComparison.Ordinal)),
                string.Create(CultureInfo.InvariantCulture, $"After placing the gauge a quarter turn below 70% (turn cost {turnCost:F2}, budget {budget}) the 90% warning appeared too early."));
            Assert.False(crossing70.Cumulative.Any(line => line.Contains("DUST EXHAUSTED", StringComparison.Ordinal)),
                string.Create(CultureInfo.InvariantCulture, $"After placing the gauge a quarter turn below 70% (turn cost {turnCost:F2}, budget {budget}) the exhaustion appeared too early."));

            // A quarter turn below 90%: the next turn crosses 90% alone
            TurnResult crossing90 = await CrossAsync(api, conversationId, conversationMark,
                budget * 0.90 - turnCost / 4, $"Show me my last invoice again, customer {CustomerCode}");
            Assert.True(crossing90.Cumulative.Any(line => line.Contains("DUST WARNING (90%)", StringComparison.Ordinal)),
                string.Create(CultureInfo.InvariantCulture, $"After placing the gauge a quarter turn below 90% (turn cost {turnCost:F2}, budget {budget}) the 90% warning did not appear."));
            Assert.False(crossing90.Cumulative.Any(line => line.Contains("DUST EXHAUSTED", StringComparison.Ordinal)),
                string.Create(CultureInfo.InvariantCulture, $"After placing the gauge a quarter turn below 90% (turn cost {turnCost:F2}, budget {budget}) the exhaustion appeared too early."));

            // A quarter turn below the whole budget: the next turn exhausts it
            TurnResult crossingExhaustion = await CrossAsync(api, conversationId, conversationMark,
                budget - turnCost / 4, "And my outstanding balance?");
            Assert.True(crossingExhaustion.Cumulative.Any(line => line.Contains("DUST EXHAUSTED", StringComparison.Ordinal)),
                string.Create(CultureInfo.InvariantCulture, $"After placing the gauge a quarter turn below the budget (turn cost {turnCost:F2}, budget {budget}) the exhaustion did not appear."));
        }
        finally
        {
            // The conversation is ended whether or not an assertion failed
            await fixture.Channel.EndConversationAsync(conversationId);
        }
    }

    /// <summary>Places the gauge at <paramref name="placeAt"/> dust on the record and plays one real turn, returning it.</summary>
    private async Task<TurnResult> CrossAsync(ChannelApiClient api, string conversationId, int conversationMark, double placeAt, string say)
    {
        // Writes the consumption straight on the record, so the turn below starts from a known gauge
        await api.QueryRecordAsync(conversationId,
            $"UPDATE dust_budget SET dust_consumed = {placeAt.ToString(CultureInfo.InvariantCulture)} WHERE id = 1;");

        // The real turn whose own charge carries the gauge over the threshold
        TurnScope scope = fixture.Observer.BeginTurn(conversationId);
        ChannelMessage message = await fixture.Channel.SendAsync(conversationId, say, TimeSpan.FromSeconds(180));

        // The turn's result carries the log from the conversation mark on, where the warning lines land
        return await fixture.Observer.CompleteTurnAsync(scope, say, message, conversationMark);
    }

    /// <summary>Reads the dust the conversation has consumed so far from its own record.</summary>
    private static async Task<double> ReadConsumedAsync(ChannelApiClient api, string conversationId)
    {
        // The single budget row holds the lifetime consumption the host's gauge is computed from
        return Convert.ToDouble(await api.QueryRecordAsync(conversationId, "SELECT dust_consumed FROM dust_budget WHERE id = 1;"), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// A conversation whose budget is already spent is refused at the gate, whatever it asks for: a command is
    /// told as its own outcome, a message as a notice. A resume then says the conversation is over. No model is
    /// reached, since the spending is written on the record rather than run. It holds on a budget of zero too,
    /// which is spent before anything is charged: run it with <c>Harness__DustBudgetPerConversation=0</c> and
    /// this test's own name as the filter, since the walk above needs a budget to walk through.
    /// </summary>
    [Fact]
    public async Task Spent_budget_refuses_commands_and_messages_at_the_gate()
    {
        Assert.SkipWhen(fixture.Options.DustBudgetPerConversation is null,
            "Dust limiting is off: run with Harness__DustBudgetPerConversation set, in this class's own invocation.");

        ChannelApiClient api = new ChannelApiClient(fixture);
        string conversationId = ChannelApiClient.NewConversationId();
        await api.SeedConversationOnRecordAsync(conversationId, activeAgent: "billing", fixture.Channel.CallbackUrl);
        await api.QueryRecordAsync(conversationId,
            $"UPDATE dust_budget SET dust_consumed = {fixture.Options.DustBudgetPerConversation!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)} WHERE id = 1;");

        // A command asked of a spent conversation is refused as that command's outcome, carrying the reason a
        // channel ends the conversation on
        Assert.Equal(HttpStatusCode.TooManyRequests, (await api.SendCommandAsync(conversationId, """{"name":"compact"}""", TestContext.Current.CancellationToken)).StatusCode);
        ChannelMessage commandRefusal = await fixture.Channel.ReceiveAsync(conversationId, TimeSpan.FromSeconds(15));
        Assert.Equal("dust_budget_exhausted", commandRefusal.ErrorReason);
        Assert.Equal("system", commandRefusal.MessageType);
        Assert.True(commandRefusal.Progress is { Command: "compact", Finished: true }, "The refusal of a command was not told as that command's outcome.");

        // A message is refused as a notice in the conversation, with no frame belonging to it
        Assert.Equal(HttpStatusCode.TooManyRequests, (await api.SendAsync(
            "POST", "/api/morgana/conversation/{id}/message", conversationId, api.HarnessToken())).StatusCode);
        ChannelMessage messageRefusal = await fixture.Channel.ReceiveAsync(conversationId, TimeSpan.FromSeconds(15));
        Assert.Equal("dust_budget_exhausted", messageRefusal.ErrorReason);
        Assert.Equal("error", messageRefusal.MessageType);
        Assert.Null(messageRefusal.Progress);

        // A channel coming back to the conversation learns at once that it is over, gauge at zero
        HttpResponseMessage resumed = await api.SendAsync("POST", "/api/morgana/conversation/{id}/resume", conversationId, api.HarnessToken());
        JsonElement body = await resumed.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(0.0, body.GetProperty("dustLevel").GetDouble());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("dustExhaustedMessage").GetString()), "A resume of a spent conversation did not say it is over.");
    }
}
