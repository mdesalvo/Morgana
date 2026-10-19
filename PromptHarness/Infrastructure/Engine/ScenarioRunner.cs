using System.Globalization;
using System.Text;
using Microsoft.Extensions.Configuration;
using Morgana.AI;
using Morgana.Contracts;
using PromptHarness.Infrastructure.Reporting;
using PromptHarness.Infrastructure.Wiring;
using Xunit;

namespace PromptHarness.Infrastructure.Engine;

/// <summary>
/// Replays a scenario N times against the live instance and reports how many runs held.
/// </summary>
/// <remarks>
/// <para>Repetition with a threshold, rather than a single pass/fail, is the only honest shape for
/// a suite whose system under test is a language model: a prompt that produces the right behaviour
/// four times out of five is materially different from one that produces it once and a single run
/// cannot tell those apart. The threshold makes the flakiness budget explicit per scenario instead
/// of hiding it in a retry.</para>
///
/// <para>Each run opens its own conversation, so nothing leaks between runs: fresh session, fresh
/// context, fresh <c>shared_context</c> registry.</para>
/// </remarks>
public sealed class ScenarioRunner
{
    /// <summary>Channel used to drive conversations.</summary>
    private readonly HarnessChannel channel;

    /// <summary>Observer supplying the structural signals.</summary>
    private readonly TurnObserver observer;

    /// <summary>Judge for the natural-language propositions.</summary>
    private readonly LLMJudge judge;

    /// <summary>Harness knobs (defaults for runs, thresholds and timeouts).</summary>
    private readonly HarnessOptions options;

    /// <summary>Provider and models under test, recorded in the harness — a token count without them means nothing.</summary>
    private readonly string llmDescriptor;

    /// <summary>Binds the engine to the channel and the observer and the judge it drives a run with.</summary>
    public ScenarioRunner(HarnessChannel channel, TurnObserver observer, LLMJudge judge, HarnessOptions options, IConfiguration configuration)
    {
        this.channel = channel;
        this.observer = observer;
        this.judge = judge;
        this.options = options;

        // Every harness file records the models its token counts were measured on.
        llmDescriptor = DescribeLlm(configuration);
    }

    /// <summary>
    /// Renders the provider and the model bound to each tier, e.g.
    /// <c>Economy=Anthropic/claude-haiku-5-5, Efficiency=Anthropic/claude-sonnet-5-5, Performance=Anthropic/claude-opus-5-5</c>. Recorded in
    /// every harness file: a token count is only comparable against the same models.
    /// </summary>
    private static string DescribeLlm(IConfiguration configuration)
    {
        // Walk the three tiers and read the provider and model id bound to each, so a harness row is
        // always legible without cross-referencing appsettings.
        IEnumerable<string> tiers = Enum.GetValues<Morgana.AI.Records.LLMTier>()
            .Select(tier => $"{tier}={configuration[$"Morgana:LLM:Tiers:{tier}:Provider"] ?? "(unknown)"}/{configuration[$"Morgana:LLM:Tiers:{tier}:Options:ModelId"] ?? "(unset)"}");

        return string.Join(", ", tiers);
    }

    /// <summary>Loads a scenario by id and runs it.</summary>
    public Task<ScenarioOutcome> RunAsync(string scenarioId)
        => RunAsync(ScenarioLoader.Load(scenarioId));

    /// <summary>Runs a scenario the configured number of times.</summary>
    public async Task<ScenarioOutcome> RunAsync(ScenarioDefinition scenario)
    {
        int runs = scenario.Runs ?? options.DefaultRuns;
        // Clamped to the actual run count: a scenario cannot require more passes than it has runs,
        // which would make it permanently unpassable if MinPasses were ever misconfigured above Runs.
        int required = Math.Min(scenario.MinPasses ?? options.DefaultMinPasses, runs);

        // Sequential, not parallel — see AssemblyInfo.cs: the log-based context-access correlation
        // is time-window based and only sound while one turn is in flight at a time.
        List<RunOutcome> outcomes = [];
        for (int index = 1; index <= runs; index++)
            outcomes.Add(await RunOnceAsync(scenario, index));

        ScenarioOutcome outcome = new ScenarioOutcome(scenario, required, outcomes);

        // Deliberately not Console.WriteLine: stdout is teed into the host log capture and a line
        // written there would resurface inside the next turn's captured log.
        TestContext.Current.TestOutputHelper?.WriteLine($"[Harness] {outcome.Summary()}");

        // Both writers run regardless of pass/fail — the journey row is written every time (a
        // phase's history includes its failures) and the failure log self-manages whether to
        // write or delete based on whether every run held.
        HarnessWriter.Write(outcome, llmDescriptor, options.Phase, options.HarnessDirectory);
        FailureLog.Write(outcome, options.Phase, options.HarnessDirectory);

        return outcome;
    }

    /// <summary>Plays the scripted conversation once and collects everything that did not hold.</summary>
    private async Task<RunOutcome> RunOnceAsync(ScenarioDefinition scenario, int index)
    {
        List<string> failures = [];
        List<string> transcript = [];
        List<TurnResult> turns = [];
        TokenUsage tokens = TokenUsage.Zero;
        string? conversationId = null;

        try
        {
            // A brand-new conversation per run: fresh session, fresh context, fresh shared_context
            // registry — nothing from a previous run's turns can leak into this one's assertions.
            TimeSpan timeout = TimeSpan.FromSeconds(options.TurnTimeoutSeconds);

            // Only the one scenario group that means to exercise MorganaChannelAdapter opts into
            // the degraded profile; every other scenario keeps opening on the default full-capability
            // channel, unchanged from before this branch existed.
            (string opened, ChannelMessage lastReceived) = scenario.DegradedChannel is true
                ? await channel.StartConversationAsync(timeout, HarnessChannel.DegradedCapabilities, HarnessChannel.DegradedChannelName)
                : await channel.StartConversationAsync(timeout);
            conversationId = opened;

            // Round-robins one actor per run for a scenario pooling several — see
            // ScenarioDefinition.CustomerCodes' own remarks for why "more stock" cannot substitute
            // for "a different customer" when the dispositive action is one-time-only per customer.
            string? customerCode = scenario.CustomerCodes is { Count: > 0 } codes
                ? codes[(index - 1) % codes.Count]
                : null;

            foreach (TurnDefinition turnDefinition in scenario.Turns)
            {
                string say;
                if (turnDefinition.Press is { } pressedTool)
                {
                    // The button's value is written at runtime, so the scenario names the tool of an action button or the exact id of a welcome or disambiguation button and the run reads the value from what was offered.
                    QuickReply? pressed = (lastReceived.QuickReplies ?? [])
                        .FirstOrDefault(quickReply => string.Equals(Records.ClosingDeclaration.ActionTool(quickReply.Id), pressedTool, StringComparison.Ordinal)
                            || string.Equals(quickReply.Id, pressedTool, StringComparison.Ordinal));

                    if (pressed is null)
                    {
                        // Later turns presuppose the press happened, so the run stops here like an aborted one.
                        failures.Add($"turn {transcript.Count + 1}: no button leading to or identified as '{pressedTool}' was offered");
                        break;
                    }

                    say = pressed.Value;
                }
                else
                    say = customerCode is null
                        ? turnDefinition.Say
                        : turnDefinition.Say.Replace("{{customerCode}}", customerCode, StringComparison.Ordinal);

                // Mark, send, then close the window: BeginTurn/CompleteTurnAsync bracket exactly
                // the observation period this one turn's send-and-reply spans.
                TurnScope scope = observer.BeginTurn(conversationId);
                ChannelMessage message = await channel.SendAsync(conversationId, say, timeout);
                lastReceived = message;
                TurnResult turn = await observer.CompleteTurnAsync(scope, say, message);

                transcript.Add(turn.Describe());
                turns.Add(turn);

                // Accumulated before the judge runs, so the measurement stays Morgana's cost only.
                tokens += turn.Tokens;

                // Fetched only for the turns that ask about it: every other structural signal is
                // already in hand when the turn ends, while this one is a round trip to the host.
                IReadOnlyList<MorganaChatMessage>? history =
                    turnDefinition.Expect is { AsksAboutHistory: true }
                        ? await channel.GetHistoryAsync(conversationId)
                        : null;

                // The structural layer runs first and is deterministic — no Expect block at all
                // means nothing is checked and the turn cannot fail structurally.
                IReadOnlyList<string> structural = turnDefinition.Expect is null
                    ? []
                    : ExpectationChecker.Check(turnDefinition.Expect, turn, history);

                failures.AddRange(structural.Select(failure => $"turn {transcript.Count}: {failure}"));

                // The judge is skipped once the turn already failed structurally: the run is lost
                // either way and judging costs a live LLM call.
                if (structural.Count == 0)
                {
                    // The judge's calls are windowed apart from the turn's, so a verdict is read on its evidence
                    // in the failure log without ever entering the turn's cost.
                    int judgeMark = observer.ModelCallMark();
                    IReadOnlyList<string> judged = await judge.EvaluateAsync(turnDefinition, turn);
                    turns[^1] = turn with { JudgeModelCalls = observer.ModelCallsSince(judgeMark) };
                    failures.AddRange(judged.Select(failure => $"turn {transcript.Count}: {failure}"));
                }
            }
        }
        catch (Exception ex)
        {
            // Anything unhandled — a timeout, a dropped connection, a malformed response — turns
            // into a single failure entry rather than propagating and aborting every remaining run
            // of the scenario; the loop in RunAsync keeps going for the runs after this one.
            failures.Add($"run aborted: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            // Runs even when StartConversationAsync itself threw (conversationId stays null in
            // that case, so there is nothing to end) or when a later turn aborted mid-conversation.
            if (conversationId is not null)
                await channel.EndConversationAsync(conversationId);
        }

        return new RunOutcome(index, failures, transcript, tokens, turns);
    }
}
