using System.Globalization;
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
/// <param name="Tokens">Token usage summed over the run's turns, the judge's calls excluded as the harness's cost and not Morgana's.</param>
/// <param name="Turns">Every turn played, kept whole so a failing run's model calls can be written out.</param>
/// <param name="ModelCalls">The LLM calls of a run made of no turns, as a calibration case that asks a guard directly; a run made of turns carries its calls on each turn instead.</param>
public sealed record RunOutcome(
    int Index,
    IReadOnlyList<string> Failures,
    IReadOnlyList<string> Transcript,
    TokenUsage Tokens,
    IReadOnlyList<TurnResult> Turns,
    IReadOnlyList<ModelCall>? ModelCalls = null)
{
    /// <summary>The LLM calls of a run that has no turns, never null.</summary>
    public IReadOnlyList<ModelCall> Calls => ModelCalls ?? [];

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
        // The verdict, what the scenario protects and what a run costs, ahead of the runs that failed.
        StringBuilder report = new StringBuilder();
        report.AppendLine(CultureInfo.InvariantCulture, $"Scenario '{Scenario.Id}': {Passes}/{Runs.Count} runs passed, {Required} required.");
        report.AppendLine(Scenario.Description);
        report.AppendLine(CultureInfo.InvariantCulture, $"Tokens per run: {CallsPerRun:F1} LLM calls, in={InputTokensPerRun}, out={OutputTokensPerRun}, cacheRead={(Runs.Count == 0 ? 0 : TotalTokens.CacheReadTokens / Runs.Count)}");

        // Only the failing runs get a section: one that failed under a cleared threshold is still worth reading, while
        // a passing run adds nothing a reader needs to diagnose what went wrong.
        foreach (RunOutcome run in Runs.Where(run => !run.Passed))
        {
            // The run's number, then each of its violated expectations.
            report.AppendLine();
            report.AppendLine(CultureInfo.InvariantCulture, $"--- run {run.Index} ---");
            foreach (string failure in run.Failures)
                report.AppendLine(CultureInfo.InvariantCulture, $"  ✗ {failure}");

            // The whole conversation follows the failures, so the reader sees what produced them.
            foreach (string turn in run.Transcript)
            {
                report.AppendLine();
                report.AppendLine(turn);
            }
        }

        // The report the failing test prints.
        return report.ToString();
    }
}

/// <summary>
/// Replays a scenario N times against the live instance and reports how many runs held, each run on a conversation of its own.
/// A language model is the system under test, so a single run cannot tell a prompt that holds four times out of five from one
/// that holds once: the threshold states each scenario's tolerance instead of hiding it in a retry.
/// </summary>
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

    /// <summary>Binds the runner to the channel it plays turns on, the observer of their signals, the judge and the harness's options.</summary>
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
        // The scenario's own run count; the harness default when it states none.
        int runs = scenario.Runs ?? options.DefaultRuns;

        // Clamped to the actual run count: a scenario cannot require more passes than it has runs,
        // which would make it permanently unpassable if MinPasses were ever misconfigured above Runs.
        int required = Math.Min(scenario.MinPasses ?? options.DefaultMinPasses, runs);

        // Sequential, not parallel — see AssemblyInfo.cs: the log-based context-access correlation
        // is time-window based and only sound while one turn is in flight at a time.
        List<RunOutcome> outcomes = [];
        for (int index = 1; index <= runs; index++)
            outcomes.Add(await RunOnceAsync(scenario, index));

        // The runs measured against the threshold.
        ScenarioOutcome outcome = new ScenarioOutcome(scenario, required, outcomes);

        // Deliberately not Console.WriteLine: stdout is teed into the host log capture and a line
        // written there would resurface inside the next turn's captured log.
        TestContext.Current.TestOutputHelper?.WriteLine($"[Harness] {outcome.Summary()}");

        // Both writers run regardless of pass/fail — the journey row is written every time (a
        // phase's history includes its failures) and the failure log self-manages whether to
        // write or delete based on whether every run held.
        HarnessWriter.Write(outcome, llmDescriptor, options.Phase, options.HarnessDirectory);
        FailureLog.Write(outcome, options.Phase, options.HarnessDirectory);

        // The outcome the test asserts on and prints when it fails.
        return outcome;
    }

    /// <summary>Plays the scripted conversation once and collects everything that did not hold.</summary>
    private async Task<RunOutcome> RunOnceAsync(ScenarioDefinition scenario, int index)
    {
        // What the run gathers turn by turn: its failures, its readable transcript, the turns whole and what they cost.
        List<string> failures = [];
        List<string> transcript = [];
        List<TurnResult> turns = [];
        TokenUsage tokens = TokenUsage.Zero;

        // Null until the conversation opens, so an aborted start leaves nothing to end.
        string? conversationId = null;

        try
        {
            // How long each turn may take to answer before the run is aborted.
            TimeSpan timeout = TimeSpan.FromSeconds(options.TurnTimeoutSeconds);

            // A brand-new conversation per run, so nothing of a previous run's turns can leak into this one's assertions. Only
            // the scenario that exercises MorganaChannelAdapter opens on the degraded profile; every other opens on the full channel.
            (string opened, ChannelMessage lastReceived) = scenario.DegradedChannel is true
                ? await channel.StartConversationAsync(timeout, HarnessChannel.DegradedCapabilities, HarnessChannel.DegradedChannelName)
                : await channel.StartConversationAsync(timeout);
            conversationId = opened;

            // One customer per run for a scenario pooling several, since the action it plays may be taken only once per customer.
            string? customerCode = scenario.CustomerCodes is { Count: > 0 } codes
                ? codes[(index - 1) % codes.Count]
                : null;

            foreach (TurnDefinition turnDefinition in scenario.Turns)
            {
                // What the user sends this turn, typed or carried by the button pressed.
                string say;

                // A pressed button: the scenario names its id and the value is read from what the last answer offered.
                if (turnDefinition.Press is { } pressedId)
                {
                    // The button's value is written at runtime, so the scenario names the button's id and the run reads the value from what was offered.
                    QuickReply? pressedButton = (lastReceived.QuickReplies ?? [])
                        .FirstOrDefault(quickReply => string.Equals(quickReply.Id, pressedId, StringComparison.Ordinal));

                    if (pressedButton is null)
                    {
                        // Later turns presuppose the press happened, so the run stops here like an aborted one.
                        failures.Add($"turn {transcript.Count + 1}: no button identified as '{pressedId}' was offered");
                        break;
                    }

                    // A press sends exactly what the button carries.
                    say = pressedButton.Value;
                }
                else
                {
                    // A typed turn, with the run's own customer where the scenario pools several.
                    say = customerCode is null
                        ? turnDefinition.Say
                        : turnDefinition.Say.Replace("{{customerCode}}", customerCode, StringComparison.Ordinal);
                }

                // The observation window opens before the send and closes on the reply, so this turn's signals are the window's alone.
                TurnScope scope = observer.BeginTurn(conversationId);
                ChannelMessage message = await channel.SendAsync(conversationId, say, timeout);

                // The buttons a later press reads are those of the last answer the user received.
                lastReceived = message;
                TurnResult turn = await observer.CompleteTurnAsync(scope, say, message);

                // The turn joins the transcript a failing run prints and the turns whose model calls the failure log writes.
                transcript.Add(turn.Describe());
                turns.Add(turn);

                // Accumulated before the judge runs, so the measurement stays Morgana's cost only.
                tokens += turn.Tokens;

                // Fetched only for the turns that ask about it: every other structural signal is
                // already in hand when the turn ends, while this one is a round trip to the host.
                IReadOnlyList<MorganaChatMessage>? history =
                    turnDefinition.Expect is { } spec && (spec.HistoryExcludesAgents is { Count: > 0 } || spec.HistoryUserMessages is not null)
                        ? await channel.GetHistoryAsync(conversationId)
                        : null;

                // The structural layer runs first and is deterministic. A turn with no Expect block still
                // answers to the rules every turn answers to, so it is checked against an empty one.
                IReadOnlyList<string> structuralFailures = ExpectationChecker.Check(turnDefinition.Expect ?? new ExpectSpec(), turn, history);

                // Each failure names the turn it belongs to.
                failures.AddRange(structuralFailures.Select(failure => $"turn {transcript.Count}: {failure}"));

                // The judge is skipped once the turn already failed structurally: the run is lost
                // either way and judging costs a live LLM call.
                if (structuralFailures.Count == 0)
                {
                    // The judge's calls are windowed apart from the turn's, so a verdict is read on its evidence
                    // in the failure log without ever entering the turn's cost.
                    int judgeMark = observer.ModelCallMark();
                    IReadOnlyList<string> judged = await judge.EvaluateAsync(turnDefinition, turn);
                    turns[^1] = turn with { JudgeModelCalls = observer.ModelCallsSince(judgeMark) };

                    // A proposition that did not come out as required fails the run like a structural failure.
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
            // The conversation is ended however the run went, so its actors do not outlive it; a start that failed left none to end.
            if (conversationId is not null)
                await channel.EndConversationAsync(conversationId);
        }

        // The run as the scenario's outcome counts it.
        return new RunOutcome(index, failures, transcript, tokens, turns);
    }
}