using System.Text;
using Morgana.Contracts;
using PromptHarness.Infrastructure.Reporting;
using PromptHarness.Infrastructure.Wiring;
using Xunit;

namespace PromptHarness.Infrastructure.Engine;

/// <summary>Outcome of a single replay of a scenario.</summary>
/// <param name="Index">1-based run number.</param>
/// <param name="Failures">Violated expectations; empty when the run passed.</param>
/// <param name="Transcript">Per-turn rendering of what happened, attached to failing runs.</param>
/// <param name="Tokens">Token usage summed over the run's turns. Judge calls are excluded: they are the harness's cost, not Morgana's.</param>
public sealed record RunOutcome(
    int Index,
    IReadOnlyList<string> Failures,
    IReadOnlyList<string> Transcript,
    TokenUsage Tokens)
{
    /// <summary>Whether every expectation of every turn held.</summary>
    public bool Passed => Failures.Count == 0;
}

/// <summary>Aggregate outcome of a scenario over its configured number of runs.</summary>
/// <param name="Scenario">The scenario that was run.</param>
/// <param name="Required">Number of passing runs required.</param>
/// <param name="Runs">Individual run outcomes.</param>
public sealed record ScenarioOutcome(ScenarioDefinition Scenario, int Required, IReadOnlyList<RunOutcome> Runs)
{
    /// <summary>How many runs passed.</summary>
    public int Passes => Runs.Count(run => run.Passed);

    /// <summary>Whether the scenario met its threshold.</summary>
    public bool Passed => Passes >= Required;

    /// <summary>Token usage summed over every run.</summary>
    public TokenUsage TotalTokens => Runs.Aggregate(TokenUsage.Zero, (total, run) => total + run.Tokens);

    /// <summary>Mean input tokens per run — the number a prompt revision is expected to move.</summary>
    public long InputTokensPerRun => Runs.Count == 0 ? 0 : TotalTokens.InputTokens / Runs.Count;

    /// <summary>Mean output tokens per run.</summary>
    public long OutputTokensPerRun => Runs.Count == 0 ? 0 : TotalTokens.OutputTokens / Runs.Count;

    /// <summary>Mean LLM round trips per run.</summary>
    public double CallsPerRun => Runs.Count == 0 ? 0 : (double)TotalTokens.Calls / Runs.Count;

    /// <summary>One-line summary, emitted on every run, pass or fail.</summary>
    public string Summary()
        => $"'{Scenario.Id}': {Passes}/{Runs.Count} passed (need {Required}) · "
         + $"per run: {CallsPerRun:F1} LLM calls, in={InputTokensPerRun}, out={OutputTokensPerRun}, "
         + $"cacheRead={(Runs.Count == 0 ? 0 : TotalTokens.CacheReadTokens / Runs.Count)}";

    /// <summary>Human-readable report, printed on failure.</summary>
    public string Report()
    {
        StringBuilder report = new StringBuilder();
        report.AppendLine($"Scenario '{Scenario.Id}': {Passes}/{Runs.Count} runs passed, {Required} required.");
        report.AppendLine(Scenario.Description);
        report.AppendLine($"Tokens per run: {CallsPerRun:F1} LLM calls, in={InputTokensPerRun}, out={OutputTokensPerRun}, cacheRead={(Runs.Count == 0 ? 0 : TotalTokens.CacheReadTokens / Runs.Count)}");

        // Only the failing runs get a section — a scenario that cleared its threshold with, say,
        // 4/5 still has one failing run worth reading (see FailureLog's own reasoning for why),
        // while the passing runs add nothing a reader needs to diagnose what went wrong.
        foreach (RunOutcome run in Runs.Where(run => !run.Passed))
        {
            report.AppendLine();
            report.AppendLine($"--- run {run.Index} ---");
            foreach (string failure in run.Failures)
                report.AppendLine($"  ✗ {failure}");

            // The full per-turn transcript follows the failure list, so the reader sees not just
            // *what* was violated but the entire conversation that produced it.
            foreach (string turn in run.Transcript)
            {
                report.AppendLine();
                report.AppendLine(turn);
            }
        }

        return report.ToString();
    }
}

/// <summary>
/// Replays a scenario N times against the live instance and reports how many runs held.
/// </summary>
/// <remarks>
/// <para>Repetition with a threshold, rather than a single pass/fail, is the only honest shape for
/// a suite whose system under test is a language model: a prompt that produces the right behaviour
/// four times out of five is materially different from one that produces it once, and a single run
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

    public ScenarioRunner(HarnessChannel channel, TurnObserver observer, LLMJudge judge, HarnessOptions options, string llmDescriptor)
    {
        this.channel = channel;
        this.observer = observer;
        this.judge = judge;
        this.options = options;
        this.llmDescriptor = llmDescriptor;
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

        // Deliberately not Console.WriteLine: stdout is teed into the host log capture, and a line
        // written there would resurface inside the next turn's captured log.
        TestContext.Current.TestOutputHelper?.WriteLine($"[Harness] {outcome.Summary()}");

        // Both writers run regardless of pass/fail — the journey row is written every time (a
        // phase's history includes its failures), and the failure log self-manages whether to
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
            (string opened, ChannelMessage _) = scenario.DegradedChannel is true
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
                string say = customerCode is null
                    ? turnDefinition.Say
                    : turnDefinition.Say.Replace("{{customerCode}}", customerCode, StringComparison.Ordinal);

                // Mark, send, then close the window: BeginTurn/CompleteTurnAsync bracket exactly
                // the observation period this one turn's send-and-reply spans.
                TurnScope scope = observer.BeginTurn(conversationId);
                ChannelMessage message = await channel.SendAsync(conversationId, say, timeout);
                TurnResult turn = await observer.CompleteTurnAsync(scope, say, message);

                transcript.Add(turn.Describe());

                // Accumulated before the judge runs, so the measurement stays Morgana's cost only.
                tokens += turn.Tokens;

                // The structural layer runs first and is deterministic — no Expect block at all
                // means nothing is checked and the turn cannot fail structurally.
                IReadOnlyList<string> structural = turnDefinition.Expect is null
                    ? []
                    : ExpectationChecker.Check(turnDefinition.Expect, turn);

                failures.AddRange(structural.Select(failure => $"turn {transcript.Count}: {failure}"));

                // The judge is skipped once the turn already failed structurally: the run is lost
                // either way, and judging costs a live LLM call.
                if (structural.Count == 0)
                {
                    IReadOnlyList<string> judged = await judge.EvaluateAsync(turnDefinition, turn);
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

        return new RunOutcome(index, failures, transcript, tokens);
    }
}
