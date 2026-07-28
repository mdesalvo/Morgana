using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

namespace PromptHarness.Scenarios;

/// <summary>
/// Keeps each scenario's journey: one versioned file per scenario, one row per revision phase,
/// carrying the pass rate and the token cost of a run.
/// </summary>
/// <remarks>
/// <para><strong>Why a journey and not a snapshot.</strong> The point of the harness is comparison,
/// and a file that is overwritten on every run answers "where is it now" while losing "where it came
/// from". The history is in git, but only for a reader who knows which commits to diff — the
/// artefact itself said nothing. Here the movement is inside the file: <c>v0</c> is the original
/// assessment, and every phase after it shows what the revision bought or cost.</para>
///
/// <para>Re-running a phase replaces its row rather than appending a second one. A phase is a state
/// of the prose, not a count of how many times it was measured: the three runs it took to settle
/// A2.1 are one row, and the diff against A2.0 stays readable.</para>
///
/// <para>A row is deliberately naked — numbers only. What a movement <em>means</em>, and which of
/// them were regressions worth reverting, belongs to <c>JOURNEY.md</c> next to these files, written
/// by hand.</para>
/// </remarks>
public static class BaselineWriter
{
    /// <summary>Header of the journey table, rewritten whenever a file is created.</summary>
    private const string TableHeader =
        "| phase | recorded | passed | required | llm calls | input | output | cache read |\n"
      + "|---|---|---:|---:|---:|---:|---:|---:|";

    /// <summary>Matches a table row and captures its phase, so a re-run of the same phase replaces it.</summary>
    private static readonly Regex RowPattern = new Regex(@"^\| *(?<phase>[^|]+?) *\|", RegexOptions.Multiline);

    /// <summary>Directory holding the journey files, resolved from this file's own compile-time path.</summary>
    public static string Directory => Path.Combine(ProjectDirectory(), "Baseline");

    /// <summary>Appends (or replaces) this phase's row in the scenario's journey.</summary>
    public static void Write(ScenarioOutcome outcome, string llmDescriptor, string phase)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);

            string path = Path.Combine(Directory, $"{outcome.Scenario.Id}.md");
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
    /// This project's directory, taken from the compile-time path of this very file. The output
    /// directory would be the obvious place to write to and the wrong one: nothing there is
    /// versioned, and an unversioned journey is not a journey.
    /// </summary>
    private static string ProjectDirectory([CallerFilePath] string sourceFilePath = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFilePath)!, ".."));
}
