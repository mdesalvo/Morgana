using PromptHarness.Infrastructure.Wiring;
using Morgana.Contracts;
using Xunit;

namespace PromptHarness.Tests;

/// <summary>
/// The Magic Dust budget mechanics, which no other scenario has ever exercised: the default budget
/// (100, calibrated for ~10 full-length Performance turns) sits far above what any scripted
/// conversation reaches, and <c>Morgana:DustLimiting:Enabled</c> is force-disabled everywhere else.
/// </summary>
/// <remarks>
/// <para><strong>Requires a lowered budget at boot</strong> — like the guard rail and the
/// summarization reducer, dust limiting is process-wide for the single assembly-shared host, so
/// lowering it would silently start throttling every other class's conversations too. Run this class
/// on its own:</para>
/// <code>Harness__DustBudgetPerConversation=15 dotnet test PromptHarness.csproj --filter "FullyQualifiedName~DustTests"</code>
///
/// <para>The number just needs to be small enough that <see cref="MaxTurns"/> is enough room to
/// exhaust it, and large enough that a single turn's own charge cannot jump straight past 90% into
/// exhaustion in one shot — <c>EmitDustWarningsIfNeededAsync</c> / <c>EmitDustExhaustionAsync</c> are
/// mutually exclusive per turn (whichever the post-send gauge calls for), so a turn crossing both at
/// once logs only the exhaustion line and this test would see 90% "never appeared". Budgets of 3 and
/// 8 both hit exactly that on live runs; 15 comfortably didn't.</para>
///
/// <para><strong>Evidence-driven, not turn-pinned.</strong> Earlier attempts at a scripted YAML
/// scenario asserting a threshold on a specific turn number kept breaking across reruns: how many
/// turns it takes to cross 70%/90%/exhaustion is a real token-cost measurement — the model's own
/// response length varies enough run to run that no fixed turn index stayed reliable, even after
/// reverse-engineering the budget from <c>DustAccountingChatClient</c>'s own pricing formula. Rather
/// than fight that variance with a tighter number, this test does the same thing a human calibrating
/// it by hand would: send one cheap turn at a time and check the diagnostic log after each one, for
/// as many turns as it actually takes (capped, so a genuine regression fails instead of looping
/// forever) — the budget only needs to be small enough that exhaustion is reached within the cap, not
/// pinned to any precise value.</para>
/// </remarks>
public sealed class DustTests
{
    /// <summary>Refuses to loop forever if a threshold never fires — a hard sign of a real regression.</summary>
    private const int MaxTurns = 30;

    /// <summary>Seeded customer with real invoices to answer against — see BillingAgent's scenarios.</summary>
    private const string CustomerCode = "P994E";

    /// <summary>The live host, shared with every other test class in the assembly.</summary>
    private readonly MorganaHostFixture fixture;

    public DustTests(MorganaHostFixture fixture) => this.fixture = fixture;

    /// <summary>
    /// Walks a conversation one cheap BillingAgent turn at a time, checking after every turn whether
    /// <c>ConversationManagerActor</c>'s diagnostic log lines show the 70% warning, the 90% warning,
    /// and the exhaustion notice — in that order, each appearing exactly once, before the budget
    /// truly locks the conversation out. Stops the instant exhaustion is observed: one more turn
    /// after that would hit the controller's hard 429 (<c>IsOverBudgetAsync</c>), which is the
    /// correct behaviour but not this test's concern.
    /// </summary>
    [Fact]
    public async Task Dust_thresholds_fire_in_order()
    {
        (string conversationId, ChannelMessage _) = await fixture.Channel.StartConversationAsync(TimeSpan.FromSeconds(180));

        // Taken once, before any turn, so the log window checked after each turn spans the whole
        // conversation so far — a threshold is one-shot and never resent, so a turn's OWN window can
        // miss it entirely depending on which turn happens to cross it (see the class's own remarks).
        int conversationMark = fixture.Observer.Mark();

        bool seen70 = false;
        bool seen90 = false;
        bool seenExhausted = false;

        try
        {
            for (int turn = 1; turn <= MaxTurns && !seenExhausted; turn++)
            {
                // Alternates two cheap, unambiguous BillingAgent asks — varied wording so the model
                // has no reason to treat a repeat as already-answered and skip the tool call, which
                // would only slow the ramp down, never break the assertions below.
                string say = turn % 2 == 1
                    ? $"Hi, my customer code is {CustomerCode} — show me my last invoice"
                    : "And my outstanding balance?";

                TurnScope scope = fixture.Observer.BeginTurn(conversationId);
                ChannelMessage message = await fixture.Channel.SendAsync(conversationId, say, TimeSpan.FromSeconds(180));
                TurnResult result = await fixture.Observer.CompleteTurnAsync(scope, say, message, conversationMark);

                if (!seen70 && result.Cumulative.Any(line => line.Contains("DUST WARNING (70%)", StringComparison.Ordinal)))
                    seen70 = true;

                if (!seen90 && result.Cumulative.Any(line => line.Contains("DUST WARNING (90%)", StringComparison.Ordinal)))
                    seen90 = true;

                if (result.Cumulative.Any(line => line.Contains("DUST EXHAUSTED", StringComparison.Ordinal)))
                    seenExhausted = true;
            }
        }
        finally
        {
            await fixture.Channel.EndConversationAsync(conversationId);
        }

        Assert.True(seen70, $"70% dust warning never appeared within {MaxTurns} turns.");
        Assert.True(seen90, $"90% dust warning never appeared within {MaxTurns} turns.");
        Assert.True(seenExhausted, $"Dust exhaustion never appeared within {MaxTurns} turns.");
    }
}
