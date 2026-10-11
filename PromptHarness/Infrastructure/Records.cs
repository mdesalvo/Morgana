using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.AI;
using Morgana.Contracts;
using PromptHarness.Infrastructure.Engine;

namespace PromptHarness.Infrastructure;

/// <summary>How the framework resolved one context-scoped parameter of a tool the turn called.</summary>
public enum ContextOperation
{
    /// <summary>The model omitted the value and the session held it.</summary>
    Hit,

    /// <summary>The model omitted the value and the session lacked it: the tool did not run.</summary>
    Miss,

    /// <summary>The model passed the value, which was stored.</summary>
    Set
}

/// <summary>A single context-variable access observed on a turn, in the order it happened.</summary>
/// <param name="Operation">Whether the access was a hit, a miss or a write.</param>
/// <param name="VariableName">Name the agent used — the thing the closed-vocabulary assertions are about.</param>
public sealed record ContextAccess(ContextOperation Operation, string VariableName);

/// <summary>
/// One consultation served during a turn, read from the <c>morgana.consultation</c> span.
/// </summary>
/// <remarks>
/// The only window there is onto the other side of the exchange. The asking agent's own span shows
/// that a colleague was called; this shows what the colleague did about it — which of its tools it
/// reached for and whether it ended the exchange waiting for something back, which is what a
/// colleague that demanded a value instead of answering looks like from outside.
/// </remarks>
/// <param name="Caller">Intent that asked.</param>
/// <param name="Target">Intent that answered.</param>
/// <param name="ToolsInvoked">Tools the answering agent called, in order.</param>
/// <param name="AwaitingReply">Whether the colleague ended its turn awaiting a reply.</param>
/// <param name="Question">The question as it was put to the colleague, whole.</param>
/// <param name="Answer">What the colleague answered, whole.</param>
public sealed record ConsultationObservation(
    string? Caller,
    string? Target,
    IReadOnlyList<string> ToolsInvoked,
    bool? AwaitingReply,
    string? Question,
    string? Answer);

/// <summary>
/// Everything the harness observed about one turn: what the user said, what the channel received
/// and the two structural signals read from inside the process.
/// </summary>
/// <param name="ConversationId">Conversation the turn belongs to.</param>
/// <param name="UserMessage">Text the harness sent.</param>
/// <param name="Message">Message Morgana delivered over the webhook.</param>
/// <param name="ToolsInvoked">Tool names in call order, from the <c>agent.tools_invoked</c> span attribute.</param>
/// <param name="ContextAccesses">Context reads and writes, from the host's own tool log lines.</param>
/// <param name="AgentName">Agent that handled the turn, from the <c>agent.name</c> span attribute; null when no agent span was seen (e.g. a guard rejection).</param>
/// <param name="Tokens">Token usage over every LLM call the turn made, including classification.</param>
/// <param name="LogLines">Raw host log captured for the turn, attached to failures for diagnosis.</param>
/// <param name="GuardCompliant">Verdict from the <c>morgana.guard</c> span; null when the guard rail is disabled or no guard span was seen.</param>
/// <param name="GuardViolation">Violation text from the same span when <paramref name="GuardCompliant"/> is false.</param>
/// <param name="ClassifierIntent">Intent from the <c>morgana.classifier</c> span; null on a follow-up turn, which skips classification entirely.</param>
/// <param name="ClassifierConfidence">Confidence from the same span, parsed from its string tag.</param>
/// <param name="Consultations">Consultations served during the turn, in the order they closed; empty on a turn that asked no colleague.</param>
/// <param name="CumulativeLogLines">
/// Host log lines since conversation start, not just since this turn began — empty unless the
/// caller passed a conversation-start mark into <c>TurnObserver.CompleteTurnAsync</c>. What a
/// one-shot, edge-triggered signal (a dust-budget threshold, never resent once crossed) needs: which
/// exact turn crosses it is a token-cost measurement that can shift by a turn or two between runs, so
/// checking "has this happened by now" against the whole conversation is the only way to assert on it
/// without pinning to a turn index that variance can invalidate. See <c>ExpectationChecker.CheckDust</c>.
/// </param>
/// <param name="ModelCalls">Every LLM call of the turn as the model saw it, classification included; the forensic record of a failing run.</param>
public sealed record TurnResult(
    string ConversationId,
    string UserMessage,
    ChannelMessage Message,
    IReadOnlyList<string> ToolsInvoked,
    IReadOnlyList<ContextAccess> ContextAccesses,
    string? AgentName,
    TokenUsage Tokens,
    IReadOnlyList<string> LogLines,
    bool? GuardCompliant = null,
    string? GuardViolation = null,
    string? ClassifierIntent = null,
    double? ClassifierConfidence = null,
    IReadOnlyList<ConsultationObservation>? Consultations = null,
    IReadOnlyList<string>? CumulativeLogLines = null,
    IReadOnlyList<ModelCall>? ModelCalls = null)
{
    /// <summary>Consultations served during the turn, never null.</summary>
    public IReadOnlyList<ConsultationObservation> Consulted => Consultations ?? [];

    /// <summary>Never null even when the caller didn't ask for cumulative tracking — see the parameter's own remarks.</summary>
    public IReadOnlyList<string> Cumulative => CumulativeLogLines ?? [];

    /// <summary>The turn's LLM calls as the model saw them, never null.</summary>
    public IReadOnlyList<ModelCall> Calls => ModelCalls ?? [];

    /// <summary>The host's error and critical entries during the turn, each with its message and exception lines.</summary>
    public IReadOnlyList<string> HostErrors
    {
        get
        {
            // The console logger opens an entry with its level and writes the message and the exception
            // on the indented lines below it, so an error entry runs until the next unindented line.
            List<string> errors = [];
            bool insideError = false;
            foreach (string line in LogLines)
            {
                if (line.StartsWith("fail:", StringComparison.Ordinal) || line.StartsWith("crit:", StringComparison.Ordinal))
                    insideError = true;
                else if (line.Length > 0 && !char.IsWhiteSpace(line[0]))
                    insideError = false;

                if (insideError)
                    errors.Add(line);
            }

            return errors;
        }
    }

    /// <summary>Names read from the session, whether the read hit or missed.</summary>
    public IReadOnlyList<string> ContextReads
        => [.. ContextAccesses.Where(a => a.Operation != ContextOperation.Set).Select(a => a.VariableName)];

    /// <summary>Names the model passed and the framework stored.</summary>
    public IReadOnlyList<string> ContextWrites
        => [.. ContextAccesses.Where(a => a.Operation == ContextOperation.Set).Select(a => a.VariableName)];

    /// <summary>Text of the delivered message, never null.</summary>
    public string Text => Message.Text ?? string.Empty;

    /// <summary>Quick replies delivered with the message, empty when none.</summary>
    public IReadOnlyList<QuickReply> QuickReplies => Message.QuickReplies ?? [];

    /// <summary>A compact rendering of the turn, used in assertion failure messages.</summary>
    // One line per structural signal the harness can see, so a failure message shows everything
    // ExpectationChecker could have checked against, not just the one property that failed.
    public string Describe()
    {
        // Separator after each line that is printed only when present: the next line of the failure
        // message starts indented under it.
        const string continuation = "\n            ";

        StringBuilder description = new StringBuilder();
        description.Append("user: ").Append(UserMessage).Append('\n');

        // The guard verdict exists only when the guard rail is enabled and a guard span was seen.
        if (GuardCompliant is not null)
            description.Append($"guard: compliant={GuardCompliant} | violation={GuardViolation ?? "(none)"}").Append(continuation);

        // The classifier verdict is absent on a follow-up turn, which skips classification.
        if (ClassifierIntent is not null)
            description.Append($"classifier: intent={ClassifierIntent} | confidence={ClassifierConfidence?.ToString("F2", CultureInfo.InvariantCulture) ?? "(unknown)"}").Append(continuation);

        description.Append($"agent: {AgentName ?? "(no agent span)"} | completed={Message.AgentCompleted} | quickReplies={QuickReplies.Count} | richCard={(Message.RichCard is null ? "absent" : "present")}").Append('\n');
        description.Append($"tools: {(ToolsInvoked.Count == 0 ? "(none)" : string.Join(", ", ToolsInvoked))}").Append('\n');

        // Consultations are listed only on a turn that asked a colleague.
        if (Consultations is { Count: > 0 })
        {
            description.Append(string.Join(continuation, Consultations.Select(consultation =>
                $"consulted {consultation.Target}: tools={(consultation.ToolsInvoked.Count == 0 ? "(none)" : string.Join("/", consultation.ToolsInvoked))} | awaitingReply={consultation.AwaitingReply}\n              asked: {consultation.Question}\n              replied: {consultation.Answer}")));
            description.Append(continuation).Append('\n');
        }

        description.Append($"tokens: {Tokens}").Append('\n');
        description.Append($"context: {(ContextAccesses.Count == 0 ? "(none)" : string.Join(", ", ContextAccesses.Select(access => $"{access.Operation}:{access.VariableName}")))}").Append('\n');
        description.Append($"text: {Text}");

        // Host errors close the description, each on its own line below a heading.
        if (HostErrors.Count > 0)
            description.Append("\nhost errors:\n").Append(string.Join("\n", HostErrors));

        return description.ToString();
    }
}

/// <summary>
/// Token usage aggregated over the LLM calls of a turn — the measurement A2 has to move.
/// </summary>
/// <param name="InputTokens">Prompt tokens billed at full rate.</param>
/// <param name="OutputTokens">Completion tokens.</param>
/// <param name="CacheReadTokens">Prompt tokens served from the provider's prompt cache.</param>
/// <param name="CacheWriteTokens">Prompt tokens written into the cache.</param>
/// <param name="Calls">Number of LLM round trips — the multiplier that makes the fixed payload expensive.</param>
public sealed record TokenUsage(
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    int Calls)
{
    /// <summary>The empty measurement and the seed of every aggregation.</summary>
    public static readonly TokenUsage Zero = new TokenUsage(0, 0, 0, 0, 0);

    /// <summary>Sums two measurements.</summary>
    public static TokenUsage operator +(TokenUsage left, TokenUsage right)
        => new TokenUsage(
            left.InputTokens + right.InputTokens,
            left.OutputTokens + right.OutputTokens,
            left.CacheReadTokens + right.CacheReadTokens,
            left.CacheWriteTokens + right.CacheWriteTokens,
            left.Calls + right.Calls);

    /// <summary>Compact rendering for transcripts and harness files.</summary>
    public override string ToString()
        => $"{Calls} call(s), in={InputTokens}, out={OutputTokens}, cacheRead={CacheReadTokens}, cacheWrite={CacheWriteTokens}";
}

/// <summary>One LLM call as the model saw it: its cost, its instructions, its tools, the messages it read and its answer.</summary>
/// <remarks>The texts are the OpenTelemetry <c>gen_ai.*</c> content attributes, as JSON; null when the host records no content.</remarks>
public sealed record ModelCall(
    TokenUsage Usage,
    string? Instructions,
    string? ToolDefinitions,
    string? InputMessages,
    string? OutputMessages);

/// <summary>
/// Marks the start of a turn's observation window: where the log stood and how many agent, guard
/// and classifier spans the conversation had already produced.
/// </summary>
/// <param name="ConversationId">Conversation being observed.</param>
/// <param name="LogMark">Index into the captured log at the moment the turn was sent.</param>
/// <param name="SpanCount">Agent spans already recorded for the conversation.</param>
/// <param name="GuardSpanCount">Guard spans already recorded for the conversation.</param>
/// <param name="ConsultationSpanCount">Consultation spans already recorded for the conversation.</param>
/// <param name="ClassifierSpanCount">Classifier spans already recorded for the conversation.</param>
/// <param name="LlmSpanCount">LLM spans already recorded, process-wide.</param>
public sealed record TurnScope(string ConversationId, int LogMark, int SpanCount, int GuardSpanCount, int ClassifierSpanCount, int LlmSpanCount, int ConsultationSpanCount);

/// <summary>Outcome of a single replay of a scenario.</summary>
/// <param name="Index">1-based run number.</param>
/// <param name="Failures">Violated expectations; empty when the run passed.</param>
/// <param name="Transcript">Per-turn rendering of what happened, attached to failing runs.</param>
/// <param name="Tokens">Token usage summed over the run's turns. Judge calls are excluded: they are the harness's cost, not Morgana's.</param>
/// <param name="Turns">Every turn played, kept whole so a failing run's model calls can be written out.</param>
public sealed record RunOutcome(
    int Index,
    IReadOnlyList<string> Failures,
    IReadOnlyList<string> Transcript,
    TokenUsage Tokens,
    IReadOnlyList<TurnResult> Turns)
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
        report.AppendLine(CultureInfo.InvariantCulture, $"Scenario '{Scenario.Id}': {Passes}/{Runs.Count} runs passed, {Required} required.");
        report.AppendLine(Scenario.Description);
        report.AppendLine(CultureInfo.InvariantCulture, $"Tokens per run: {CallsPerRun:F1} LLM calls, in={InputTokensPerRun}, out={OutputTokensPerRun}, cacheRead={(Runs.Count == 0 ? 0 : TotalTokens.CacheReadTokens / Runs.Count)}");

        // Only the failing runs get a section — a scenario that cleared its threshold with, say,
        // 4/5 still has one failing run worth reading (see FailureLog's own reasoning for why),
        // while the passing runs add nothing a reader needs to diagnose what went wrong.
        foreach (RunOutcome run in Runs.Where(run => !run.Passed))
        {
            report.AppendLine();
            report.AppendLine(CultureInfo.InvariantCulture, $"--- run {run.Index} ---");
            foreach (string failure in run.Failures)
                report.AppendLine(CultureInfo.InvariantCulture, $"  ✗ {failure}");

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

/// <summary>Outcome of one judged proposition.</summary>
/// <param name="Holds">Whether the judge found the proposition true of the response.</param>
/// <param name="Reason">The judge's one-line justification, surfaced in failure messages.</param>
public sealed record JudgeVerdict(bool Holds, string Reason);

/// <summary>A <c>contextReads</c> entry split into its optional outcome prefix and its variable name.</summary>
/// <param name="Operation">The <c>Hit</c> or <c>Miss</c> prefix; null when the entry names the variable alone.</param>
/// <param name="Variable">Name of the context variable.</param>
public sealed record ContextReadEntry(string? Operation, string Variable);

/// <summary>A conversation the harness opened and the presentation message that opened it.</summary>
/// <param name="ConversationId">Id minted client-side and handed to Morgana on the start call.</param>
/// <param name="Presentation">The unsolicited message every conversation opens with.</param>
public sealed record StartedConversation(string ConversationId, ChannelMessage Presentation);

/// <summary>What the command admission gate did with a request.</summary>
/// <param name="Context">The context the gate left, whose <c>Result</c> holds the refusal when it refused.</param>
/// <param name="Ran">Whether the gate let the request go on to the action.</param>
public sealed record CommandAdmission(ActionExecutingContext Context, bool Ran);

/// <summary>One request a scripted model received.</summary>
/// <param name="Messages">The messages the model was handed.</param>
/// <param name="Options">The options of the call; the tools offered travel in them.</param>
public sealed record ModelRequest(IReadOnlyList<ChatMessage> Messages, ChatOptions? Options);
