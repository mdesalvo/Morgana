using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using PromptHarness.Infrastructure.Engine;

namespace PromptHarness.Infrastructure.Reporting;

/// <summary>
/// Keeps each scenario's journey: one local file per scenario, one row per revision phase with the pass rate and the token cost of a run.
/// A journey keeps where the prose came from beside where it is; a re-run phase replaces its row, since a phase is a state of the prose.
/// A row carries numbers only: what a movement means is written by hand in <c>JOURNEY.md</c>.
/// </summary>
public static class HarnessWriter
{
    /// <summary>Header of the journey table, rewritten whenever a file is created.</summary>
    private const string TableHeader =
        "| phase | recorded | passed | required | llm calls | input | output | cache read |\n"
      + "|---|---|---:|---:|---:|---:|---:|---:|";

    /// <summary>Matches a table row and captures its phase, so a re-run of the same phase replaces it.</summary>
    private static readonly Regex RowPattern = new Regex(@"^\| *(?<phase>[^|]+?) *\|", RegexOptions.Multiline);

    /// <summary>Writes this phase's row into the scenario's journey, replacing the row of a phase measured before.</summary>
    public static void Write(ScenarioOutcome outcome, string llmDescriptor, string phase, string harnessDirectory)
    {
        try
        {
            // The journey directory, created on the first run of a machine.
            string directory = ResolveDirectory(harnessDirectory);
            System.IO.Directory.CreateDirectory(directory);

            // One table row: phase, date, pass fraction, threshold and the cost figures averaged per run,
            // since a scenario replays several times per phase.
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

            // A scenario's first measurement creates its journey with header and description; every later run merges its row.
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
            // A read-only checkout is not a test failure either.
        }
    }

    /// <summary>Builds a fresh journey file for a scenario measured for the first time.</summary>
    private static string Create(ScenarioOutcome outcome, string llmDescriptor, string row)
    {
        // The scenario's id and description, its length and the models under test, then the table with its first row.
        StringBuilder file = new StringBuilder();
        file.AppendLine(CultureInfo.InvariantCulture, $"# {outcome.Scenario.Id}");
        file.AppendLine();
        file.AppendLine(outcome.Scenario.Description.Trim());
        file.AppendLine();
        file.AppendLine(CultureInfo.InvariantCulture, $"- turns per run: {outcome.Scenario.Turns.Count}");
        file.AppendLine(CultureInfo.InvariantCulture, $"- llm: {llmDescriptor}");
        file.AppendLine();
        file.AppendLine("Per run, averaged — Morgana's own calls only, the judge excluded:");
        file.AppendLine();
        file.AppendLine(TableHeader);
        file.AppendLine(row);

        // The whole journey file as first written.
        return file.ToString();
    }

    /// <summary>Replaces this phase's row if present, appends it otherwise and refreshes the LLM line.</summary>
    private static string Merge(string existing, string phase, string row, string llmDescriptor)
    {
        // The file line by line, without the newline that closes it.
        string[] lines = existing.TrimEnd('\n').Split('\n');

        // One pass refreshes the "- llm:" line and looks for this phase's row to replace.
        for (int index = 0; index < lines.Length; index++)
        {
            // The model bound to a tier can change between phases even when the phase name does not.
            if (lines[index].StartsWith("- llm:", StringComparison.Ordinal))
                lines[index] = $"- llm: {llmDescriptor}";

            // A row whose first cell is this phase is the one a re-run replaces.
            Match match = RowPattern.Match(lines[index]);
            if (match.Success && match.Groups["phase"].Value == phase)
            {
                // The phase is a state of the prose, so its new measurement takes the old row's place and the table ends there.
                lines[index] = row;

                return string.Join('\n', lines) + "\n";
            }
        }

        // A new phase: its row becomes the last line of the table.
        return string.Join('\n', lines) + "\n" + row + "\n";
    }

    /// <summary>Mean, guarding the empty case.</summary>
    private static long Average(long total, int count) => count == 0 ? 0 : total / count;

    /// <summary>
    /// Resolves <see cref="HarnessOptions.HarnessDirectory"/>: an absolute path as it is, a relative one against the project directory.
    /// The build output moves with the build configuration, so the project directory is read from this file's compile-time path.
    /// </summary>
    internal static string ResolveDirectory(string configured, [CallerFilePath] string sourceFilePath = "")
        // This file sits in Infrastructure/Reporting, two levels below the project root, hence the two climbs.
        => Path.IsPathRooted(configured)
            ? configured
            : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFilePath)!, "..", "..", configured));
}