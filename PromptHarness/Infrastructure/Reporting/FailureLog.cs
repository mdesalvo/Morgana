using System.Globalization;
using System.Text;
using PromptHarness.Infrastructure.Engine;
using PromptHarness.Infrastructure.Wiring;

namespace PromptHarness.Infrastructure.Reporting;

/// <summary>
/// Persists the transcript and the model calls of every run that did not hold, beside the scenario's journey, whether or not the scenario cleared its threshold.
/// The journey row says that a scenario went 2/5 and never why, while a paid run's evidence must survive a closed terminal; a scenario passing at its
/// threshold keeps its report too, since its one failing run is the warning with no margin left. The report is rewritten whole and deleted once every run holds.
/// </summary>
public static class FailureLog
{
    /// <summary>Writes the transcript of every run that failed; once every run of the scenario held, it removes a previous report instead.</summary>
    public static void Write(ScenarioOutcome outcome, string phase, string harnessDirectory)
    {
        try
        {
            // A "failures" folder under the journey directory, so the two always sit together wherever HarnessDirectory points.
            string directory = Path.Combine(HarnessWriter.ResolveDirectory(harnessDirectory), "failures");
            string path = Path.Combine(directory, $"{outcome.Scenario.Id}.log");
            string modelPath = Path.Combine(directory, $"{outcome.Scenario.Id}.model.log");

            // Not outcome.Passed: a scenario can clear its threshold with a failing run inside it;
            // that run is the one worth reading. Delete only when there is nothing to report.
            if (outcome.Passes == outcome.Runs.Count)
            {
                // A clean scenario leaves no report, since a stale one would read as current.
                File.Delete(path);
                File.Delete(modelPath);
                return;
            }

            // Says whether this file is a red or a near miss, so it is legible on its own.
            string verdict = outcome.Passed
                ? $"verdict: passed at threshold — {outcome.Runs.Count - outcome.Passes} run(s) failed, no margin left\n"
                : "verdict: FAILED — below threshold\n";

            // The report describes the latest measurement only, so it replaces whatever an earlier phase left.
            System.IO.Directory.CreateDirectory(directory);
            File.WriteAllText(path,
                $"phase: {phase}\n"
              + $"recorded: {DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}Z\n"
              + verdict
              + "\n"
              + outcome.Report());

            // What the model was handed and answered, beside the transcript: why it acted as it did is
            // read here, never inferred from its words or bought again with another run.
            File.WriteAllText(modelPath, ModelReport(outcome));
        }
        catch (IOException)
        {
            // A report that cannot be written must never fail a scenario: it records the
            // measurement, it is not part of it.
        }
        catch (UnauthorizedAccessException)
        {
            // A read-only checkout is not a test failure either.
        }
    }

    /// <summary>Renders every model call of the failing runs: instructions, tools, the messages read and the answer.</summary>
    private static string ModelReport(ScenarioOutcome outcome)
    {
        // The calls of every failing run, run by run.
        StringBuilder report = new StringBuilder();

        // A passing run has nothing to diagnose.
        foreach (RunOutcome run in outcome.Runs.Where(run => !run.Passed))
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"=== run {run.Index} ===");

            // Instructions and tools repeat on every call of the same caller and alternate between agent,
            // guard and classifier: each distinct text is written once per run and later cited by number.
            List<string> instructionTexts = [];
            List<string> toolTexts = [];

            for (int turnIndex = 0; turnIndex < run.Turns.Count; turnIndex++)
            {
                // Each turn is headed by what the user sent, so its calls are read against it.
                TurnResult turn = run.Turns[turnIndex];
                report.AppendLine();
                report.AppendLine(CultureInfo.InvariantCulture, $"--- turn {turnIndex + 1}: {turn.UserMessage}");

                // The calls Morgana made for the turn, in the order they were made.
                for (int callIndex = 0; callIndex < turn.Calls.Count; callIndex++)
                    AppendCall(report, $"[call {callIndex + 1}]", turn.Calls[callIndex], instructionTexts, toolTexts);

                // The judge's calls follow the turn's: what it was shown and what it decided, so a red
                // verdict is read on its own evidence and never bought again with another run. The judge offers no tools.
                for (int callIndex = 0; callIndex < turn.JudgeCalls.Count; callIndex++)
                    AppendCall(report, $"[judge call {callIndex + 1}]", turn.JudgeCalls[callIndex], instructionTexts, null);
            }

            // A run that has no turns, such as a calibration case, carries its calls directly:
            // its failure is read on the guard's own evidence.
            for (int callIndex = 0; callIndex < run.Calls.Count; callIndex++)
                AppendCall(report, $"[call {callIndex + 1}]", run.Calls[callIndex], instructionTexts, toolTexts);

            // A blank line ends the run's section.
            report.AppendLine();
        }

        // The whole model report.
        return report.ToString();
    }

    /// <summary>
    /// Writes one model call under its heading: its usage, its instructions and tools by number once seen, then what it read and answered.
    /// The tool lists are skipped for a caller that offers none, such as the judge, which is handed no list to number them against.
    /// </summary>
    private static void AppendCall(StringBuilder report, string heading, ModelCall call, List<string> instructionTexts, List<string>? toolTexts)
    {
        // The heading carries the call's token usage, the first thing a cost question asks.
        report.AppendLine();
        report.AppendLine(CultureInfo.InvariantCulture, $"{heading} {call.Usage}");

        // The instructions and the tools repeat across a run's calls, so each distinct text is written once.
        AppendNumbered(report, "instructions", call.Instructions, instructionTexts);
        if (toolTexts is not null)
            AppendNumbered(report, "tools", call.ToolDefinitions, toolTexts);

        // What the model was handed and what it answered, which is where why it acted as it did is read.
        report.AppendLine("input:");
        report.AppendLine(call.InputMessages ?? "(none recorded)");
        report.AppendLine("output:");
        report.AppendLine(call.OutputMessages ?? "(none recorded)");
    }

    /// <summary>Writes a repeating text in full the first time it appears in a run, by its number afterwards.</summary>
    private static void AppendNumbered(StringBuilder report, string label, string? text, List<string> seen)
    {
        // A call whose span carried no such text says so, rather than looking like one with an empty text.
        if (text is null)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"{label}: (none recorded)");
            return;
        }

        // A text already written in this run is cited by its number.
        int number = seen.IndexOf(text) + 1;
        if (number > 0)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"{label}: as {label} #{number}");
            return;
        }

        // A text met for the first time is numbered and written whole.
        seen.Add(text);
        report.AppendLine(CultureInfo.InvariantCulture, $"{label} #{seen.Count}:");
        report.AppendLine(text);
    }
}