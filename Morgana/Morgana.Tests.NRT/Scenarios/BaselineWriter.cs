using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace Morgana.Tests.NRT.Scenarios;

/// <summary>
/// Records each scenario's outcome as a versioned baseline file: pass rate plus the token cost of
/// one run.
/// </summary>
/// <remarks>
/// <para>The point of the harness is comparison, and a comparison needs a recorded "before". These
/// files are what a prompt revision is measured against — a scenario that keeps its threshold but
/// doubles its token count has not passed, and one that keeps both while shedding half its input
/// tokens is the outcome A2 is aiming for.</para>
///
/// <para>They live in the source tree, not the output directory, precisely so they can be committed
/// and diffed. The provider and models are recorded alongside, because a token count without them
/// is not a measurement of anything.</para>
/// </remarks>
public static class BaselineWriter
{
    /// <summary>Directory holding the baseline files, resolved from this file's own compile-time path.</summary>
    public static string Directory => Path.Combine(ProjectDirectory(), "Baseline");

    /// <summary>Writes (or overwrites) the baseline of one scenario.</summary>
    public static void Write(ScenarioOutcome outcome, string llmDescriptor)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);

            StringBuilder baseline = new StringBuilder();
            baseline.AppendLine($"# {outcome.Scenario.Id}");
            baseline.AppendLine();
            baseline.AppendLine(outcome.Scenario.Description.Trim());
            baseline.AppendLine();
            baseline.AppendLine($"- recorded: {DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}Z");
            baseline.AppendLine($"- llm: {llmDescriptor}");
            baseline.AppendLine($"- turns per run: {outcome.Scenario.Turns.Count}");
            baseline.AppendLine($"- runs: {outcome.Runs.Count}, passed: {outcome.Passes}, required: {outcome.Required}");
            baseline.AppendLine();
            baseline.AppendLine("Per run, averaged — Morgana's own calls only, the judge excluded:");
            baseline.AppendLine();
            baseline.AppendLine("| llm calls | input tokens | output tokens | cache read | cache write |");
            baseline.AppendLine("|---:|---:|---:|---:|---:|");
            baseline.AppendLine(
                $"| {outcome.CallsPerRun:F1} | {outcome.InputTokensPerRun} | {outcome.OutputTokensPerRun} | "
              + $"{Average(outcome.TotalTokens.CacheReadTokens, outcome.Runs.Count)} | "
              + $"{Average(outcome.TotalTokens.CacheWriteTokens, outcome.Runs.Count)} |");

            File.WriteAllText(Path.Combine(Directory, $"{outcome.Scenario.Id}.md"), baseline.ToString());
        }
        catch (IOException)
        {
            // A baseline that cannot be written must never fail a scenario: it is a record of the
            // measurement, not part of it.
        }
        catch (UnauthorizedAccessException)
        {
            // Same reasoning: read-only source tree (a CI checkout, say) is not a test failure.
        }
    }

    /// <summary>Mean, guarding the empty case.</summary>
    private static long Average(long total, int count) => count == 0 ? 0 : total / count;

    /// <summary>
    /// This project's directory, taken from the compile-time path of this very file. The output
    /// directory would be the obvious place to write to and the wrong one: nothing there is
    /// versioned, and an unversioned baseline is not a baseline.
    /// </summary>
    private static string ProjectDirectory([CallerFilePath] string sourceFilePath = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFilePath)!, ".."));
}
