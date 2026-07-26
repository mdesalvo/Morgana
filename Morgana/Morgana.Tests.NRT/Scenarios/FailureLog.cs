using System.Globalization;

namespace Morgana.Tests.NRT.Scenarios;

/// <summary>
/// Persists the failure report of a scenario that did not hold, next to its journey file.
/// </summary>
/// <remarks>
/// <para><strong>Why this exists.</strong> Every run of this suite is billed, and until now the only
/// place a failure was legible was the assertion message on a terminal: the journey row records
/// <em>that</em> a scenario went 2/5, never <em>why</em>. Lose the console — a closed window, a
/// truncated pipe, a session that ends — and the money is spent with nothing left to diagnose from.
/// The transcript is the expensive part of a run; writing it to disk costs nothing.</para>
///
/// <para>Kept out of the journey file on purpose. A journey row is a naked number that stays
/// comparable across phases; a failure report is a bulky, transient artefact of one measurement.
/// It is rewritten whole on every run of the scenario and <strong>deleted when the scenario
/// passes</strong> — a stale report from two phases ago is worse than none, because it reads as
/// current.</para>
/// </remarks>
public static class FailureLog
{
    /// <summary>Directory holding the failure reports, a subfolder of the journey directory.</summary>
    public static string Directory => Path.Combine(BaselineWriter.Directory, "failures");

    /// <summary>Writes this scenario's failure report, or removes a previous one once it passes.</summary>
    public static void Write(ScenarioOutcome outcome, string phase)
    {
        try
        {
            string path = Path.Combine(Directory, $"{outcome.Scenario.Id}.log");

            if (outcome.Passed)
            {
                File.Delete(path);
                return;
            }

            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(path,
                $"phase: {phase}\n"
              + $"recorded: {DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}Z\n\n"
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
