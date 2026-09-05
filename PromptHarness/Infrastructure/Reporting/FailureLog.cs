using System.Globalization;
using PromptHarness.Infrastructure.Engine;

namespace PromptHarness.Infrastructure.Reporting;

/// <summary>
/// Persists the transcript of every run that did not hold, next to the scenario's journey file —
/// whether or not the scenario itself cleared its threshold.
/// </summary>
/// <remarks>
/// <para><strong>Why this exists.</strong> Every run of this suite is billed and until now the only
/// place a failure was legible was the assertion message on a terminal: the journey row records
/// <em>that</em> a scenario went 2/5, never <em>why</em>. Lose the console — a closed window, a
/// truncated pipe, a session that ends — and the money is spent with nothing left to diagnose from.
/// The transcript is the expensive part of a run; writing it to disk costs nothing.</para>
///
/// <para><strong>The gate is the run, not the scenario</strong> and that distinction was learned
/// the same way as the original lesson above. A scenario at 4/5 against a threshold of 4 *passes* —
/// and under the first version of this class that verdict deleted the transcript of the one run
/// that failed inside it. That is precisely the transcript worth keeping: a scenario sitting on its
/// threshold has no margin left, so its single failing run is the early warning for the phase after
/// next. `behaviour-conversation-closure` did exactly this at A2.5.5 and the evidence went with it.
/// A report is now written whenever <em>any</em> run failed and removed only when every run held.</para>
///
/// <para>Kept out of the journey file on purpose. A journey row is a naked number that stays
/// comparable across phases; a failure report is a bulky, transient artefact of one measurement.
/// It is rewritten whole on every run of the scenario and deleted once the scenario is clean —
/// a stale report from two phases ago is worse than none, because it reads as current.</para>
/// </remarks>
public static class FailureLog
{
    /// <summary>
    /// Writes the transcript of every run that failed, or removes a previous report once every
    /// run of the scenario held. A scenario that passes *at* its threshold still leaves a report.
    /// </summary>
    public static void Write(ScenarioOutcome outcome, string phase, string harnessDirectory)
    {
        try
        {
            // Shares HarnessWriter's directory resolution (same configured root, "failures"
            // subfolder) so the two artefacts always sit next to each other regardless of where
            // HarnessDirectory points.
            string directory = Path.Combine(HarnessWriter.ResolveDirectory(harnessDirectory), "failures");
            string path = Path.Combine(directory, $"{outcome.Scenario.Id}.log");

            // Not outcome.Passed: a scenario can clear its threshold with a failing run inside it,
            // and that run is the one worth reading. Delete only when there is nothing to report.
            if (outcome.Passes == outcome.Runs.Count)
            {
                File.Delete(path);
                return;
            }

            // Says whether this file is a red or a near miss, so it is legible on its own.
            string verdict = outcome.Passed
                ? $"verdict: passed at threshold — {outcome.Runs.Count - outcome.Passes} run(s) failed, no margin left\n"
                : "verdict: FAILED — below threshold\n";

            // Rewritten whole every time, not appended to: a failure report describes the most
            // recent measurement only, so an old report from a previous phase must not survive
            // mixed in with the current one.
            System.IO.Directory.CreateDirectory(directory);
            File.WriteAllText(path,
                $"phase: {phase}\n"
              + $"recorded: {DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}Z\n"
              + verdict
              + "\n"
              + outcome.Report());
        }
        catch (IOException)
        {
            // A report that cannot be written must never fail a scenario: it records the
            // measurement, it is not part of it.
        }
        catch (UnauthorizedAccessException)
        {
            // Same reasoning: a read-only checkout is not a test failure.
        }
    }
}
