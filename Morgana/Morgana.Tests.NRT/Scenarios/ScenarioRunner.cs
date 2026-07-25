using System.Text;
using Morgana.Contracts;
using Morgana.Tests.NRT.Infrastructure;

namespace Morgana.Tests.NRT.Scenarios;

/// <summary>Outcome of a single replay of a scenario.</summary>
/// <param name="Index">1-based run number.</param>
/// <param name="Failures">Violated expectations; empty when the run passed.</param>
/// <param name="Transcript">Per-turn rendering of what happened, attached to failing runs.</param>
public sealed record RunOutcome(int Index, IReadOnlyList<string> Failures, IReadOnlyList<string> Transcript)
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

    /// <summary>Human-readable report, printed on failure.</summary>
    public string Report()
    {
        StringBuilder report = new StringBuilder();
        report.AppendLine($"Scenario '{Scenario.Id}': {Passes}/{Runs.Count} runs passed, {Required} required.");
        report.AppendLine(Scenario.Description);

        foreach (RunOutcome run in Runs.Where(run => !run.Passed))
        {
            report.AppendLine();
            report.AppendLine($"--- run {run.Index} ---");
            foreach (string failure in run.Failures)
                report.AppendLine($"  ✗ {failure}");

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
    private readonly NrtChannel channel;

    /// <summary>Observer supplying the structural signals.</summary>
    private readonly TurnObserver observer;

    /// <summary>Judge for the natural-language propositions.</summary>
    private readonly LlmJudge judge;

    /// <summary>Harness knobs (defaults for runs, thresholds and timeouts).</summary>
    private readonly NrtOptions options;

    public ScenarioRunner(NrtChannel channel, TurnObserver observer, LlmJudge judge, NrtOptions options)
    {
        this.channel = channel;
        this.observer = observer;
        this.judge = judge;
        this.options = options;
    }

    /// <summary>Loads a scenario by id and runs it.</summary>
    public Task<ScenarioOutcome> RunAsync(string scenarioId)
        => RunAsync(ScenarioLoader.Load(scenarioId));

    /// <summary>Runs a scenario the configured number of times.</summary>
    public async Task<ScenarioOutcome> RunAsync(ScenarioDefinition scenario)
    {
        int runs = scenario.Runs ?? options.DefaultRuns;
        int required = Math.Min(scenario.MinPasses ?? options.DefaultMinPasses, runs);

        List<RunOutcome> outcomes = [];
        for (int index = 1; index <= runs; index++)
            outcomes.Add(await RunOnceAsync(scenario, index));

        return new ScenarioOutcome(scenario, required, outcomes);
    }

    /// <summary>Plays the scripted conversation once and collects everything that did not hold.</summary>
    private async Task<RunOutcome> RunOnceAsync(ScenarioDefinition scenario, int index)
    {
        List<string> failures = [];
        List<string> transcript = [];
        string? conversationId = null;

        try
        {
            TimeSpan timeout = TimeSpan.FromSeconds(options.TurnTimeoutSeconds);
            (string opened, ChannelMessage _) = await channel.StartConversationAsync(timeout);
            conversationId = opened;

            foreach (TurnDefinition turnDefinition in scenario.Turns)
            {
                TurnScope scope = observer.BeginTurn(conversationId);
                ChannelMessage message = await channel.SendAsync(conversationId, turnDefinition.Say, timeout);
                TurnResult turn = await observer.CompleteTurnAsync(scope, turnDefinition.Say, message);

                transcript.Add(turn.Describe());

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
            failures.Add($"run aborted: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (conversationId is not null)
                await channel.EndConversationAsync(conversationId);
        }

        return new RunOutcome(index, failures, transcript);
    }
}
