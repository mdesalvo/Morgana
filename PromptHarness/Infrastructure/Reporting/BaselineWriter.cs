using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using PromptHarness.Infrastructure.Engine;

namespace PromptHarness.Infrastructure.Reporting;

/// <summary>
/// Keeps each scenario's journey: one local file per scenario, one row per revision phase, carrying
/// the pass rate and the token cost of a run.
/// </summary>
/// <remarks>
/// <para><strong>Why a journey and not a snapshot.</strong> The point of the harness is comparison,
/// and a file that is overwritten on every run answers "where is it now" while losing "where it came
/// from". Here the movement is inside the file: <c>v0</c> is the original assessment, and every
/// phase after it shows what the revision bought or cost.</para>
///
/// <para>Re-running a phase replaces its row rather than appending a second one. A phase is a state
/// of the prose, not a count of how many times it was measured: the three runs it took to settle
/// A2.1 are one row, and the diff against A2.0 stays readable.</para>
///
/// <para>A row is deliberately naked — numbers only. What a movement <em>means</em>, and which of
/// them were regressions worth reverting, belongs to <c>JOURNEY.md</c> next to these files, written
/// by hand.</para>
///
/// <para><strong>Not versioned.</strong> The directory is a local measurement log
/// (<see cref="HarnessOptions.BaselineDirectory"/>), not a repository artefact: every run is billed,
/// and reviewing token-count diffs as pull-request noise buys nothing. Comparison across phases
/// still works — it just happens on whoever's disk is running the suite, not in git history.</para>
/// </remarks>
public static class BaselineWriter
{
    /// <summary>Header of the journey table, rewritten whenever a file is created.</summary>
    private const string TableHeader =
        "| phase | recorded | passed | required | llm calls | input | output | cache read |\n"
      + "|---|---|---:|---:|---:|---:|---:|---:|";

    /// <summary>Matches a table row and captures its phase, so a re-run of the same phase replaces it.</summary>
    private static readonly Regex RowPattern = new Regex(@"^\| *(?<phase>[^|]+?) *\|", RegexOptions.Multiline);

    /// <summary>Appends (or replaces) this phase's row in the scenario's journey.</summary>
    public static void Write(ScenarioOutcome outcome, string llmDescriptor, string phase, string baselineDirectory)
    {
        try
        {
            string directory = ResolveDirectory(baselineDirectory);
            System.IO.Directory.CreateDirectory(directory);

            string path = Path.Combine(directory, $"{outcome.Scenario.Id}.md");
            string row =
                $"| {phase} "
              + $"| {DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} "
              + $"| {outcome.Passes}/{outcome.Runs.Count} "
              + $"| {outcome.Required} "
              + $"| {outcome.CallsPerRun.ToString("F1", CultureInfo.InvariantCulture)} "
              + $"| {outcome.InputTokensPerRun} "
              + $"| {outcome.OutputTokensPerRun} "
              + $"| {Average(outcome.TotalTokens.CacheReadTokens, outcome.Runs.Count)} |";

            File.WriteAllText(path, File.Exists(path)
                ? Merge(File.ReadAllText(path), phase, row, llmDescriptor)
                : Create(outcome, llmDescriptor, row));
        }
        catch (IOException)
        {
            // A journey that cannot be written must never fail a scenario: it records the
            // measurement, it is not part of it.
        }
        catch (UnauthorizedAccessException)
        {
            // Same reasoning: a read-only checkout is not a test failure.
        }
    }

    /// <summary>Builds a fresh journey file for a scenario measured for the first time.</summary>
    private static string Create(ScenarioOutcome outcome, string llmDescriptor, string row)
    {
        StringBuilder file = new StringBuilder();
        file.AppendLine($"# {outcome.Scenario.Id}");
        file.AppendLine();
        file.AppendLine(outcome.Scenario.Description.Trim());
        file.AppendLine();
        file.AppendLine($"- turns per run: {outcome.Scenario.Turns.Count}");
        file.AppendLine($"- llm: {llmDescriptor}");
        file.AppendLine();
        file.AppendLine("Per run, averaged — Morgana's own calls only, the judge excluded:");
        file.AppendLine();
        file.AppendLine(TableHeader);
        file.AppendLine(row);

        return file.ToString();
    }

    /// <summary>Replaces this phase's row if present, appends it otherwise, and refreshes the LLM line.</summary>
    private static string Merge(string existing, string phase, string row, string llmDescriptor)
    {
        string[] lines = existing.TrimEnd('\n').Split('\n');

        for (int index = 0; index < lines.Length; index++)
        {
            if (lines[index].StartsWith("- llm:", StringComparison.Ordinal))
                lines[index] = $"- llm: {llmDescriptor}";

            Match match = RowPattern.Match(lines[index]);
            if (match.Success && match.Groups["phase"].Value == phase)
            {
                lines[index] = row;

                return string.Join('\n', lines) + "\n";
            }
        }

        return string.Join('\n', lines) + "\n" + row + "\n";
    }

    /// <summary>Mean, guarding the empty case.</summary>
    private static long Average(long total, int count) => count == 0 ? 0 : total / count;

    /// <summary>
    /// Resolves <see cref="HarnessOptions.BaselineDirectory"/>: an absolute path is used as-is; a
    /// relative one resolves against the project directory (taken from this file's own compile-time
    /// path), not the build output — a location that shifts with the build configuration is not a
    /// stable place to point a relative path at.
    /// </summary>
    internal static string ResolveDirectory(string configured, [CallerFilePath] string sourceFilePath = "")
        => Path.IsPathRooted(configured)
            ? configured
            : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFilePath)!, "..", configured));
}
