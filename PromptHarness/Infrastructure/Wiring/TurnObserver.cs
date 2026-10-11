using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Morgana.AI;
using Morgana.Contracts;

namespace PromptHarness.Infrastructure.Wiring;

/// <summary>
/// Reads the two structural signals a turn leaves behind inside the host process: the
/// <c>morgana.agent</c> span and the context-access log lines <c>MorganaToolAdapter</c> writes as it
/// resolves a tool's context-scoped parameters (HIT/MISS/SET).
/// </summary>
/// <remarks>
/// <para>Neither signal exists for the harness's benefit — both are production instrumentation the
/// suite merely listens to. That is the point: an assertion that needs a hook the framework does
/// not otherwise have is an assertion measuring the test rig.</para>
///
/// <para>The <see cref="ActivityListener"/> also has a side effect worth knowing: it makes
/// <c>ActivitySource.StartActivity</c> return real activities even with every exporter disabled,
/// which is precisely how the suite gets span data without an OTLP collector in the loop.</para>
/// </remarks>
public sealed class TurnObserver : IDisposable
{
    /// <summary>
    /// Matches the context-access log lines emitted by <c>MorganaToolAdapter</c>, built from the framework's
    /// own message template rather than from a copy of it.
    /// </summary>
    /// <remarks>
    /// These lines are the only place a context variable's NAME becomes observable — a span carries
    /// tool names and no data — so the whole context-handling group rests on them and a reworded
    /// log line would otherwise turn into a silent pass. Deriving the pattern from
    /// <see cref="Constants.ObservableLogs"/> makes that impossible: the wording travels and a
    /// renamed placeholder stops matching here at the same commit it changes there.
    /// </remarks>
    private static readonly Regex ContextAccessPattern = new Regex(
        PatternFrom(
            Constants.ObservableLogs.ContextAccessHead,
            ("{MorganaToolName}", Regex.Escape(Constants.ObservableLogs.ToolName)),
            ("{Name}", "[^)]*"),
            ("{Operation}", $"(?<op>{Constants.ObservableLogs.Hit}|{Constants.ObservableLogs.Miss}|{Constants.ObservableLogs.Set})"),
            ("{VariableName}", "(?<name>[^']*)")),
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Turns a logging message template into a regex: everything outside the placeholders is matched
    /// literally and each named placeholder is replaced by what the caller wants read out of it.
    /// </summary>
    /// <remarks>
    /// The template is escaped FIRST and the placeholders are then found in their escaped form, so a
    /// template containing regex metacharacters — the parentheses around the tool's class name do —
    /// stays literal. A placeholder the caller does not name is left escaped and simply never matches,
    /// which is the loud failure wanted: a template that grew a field the harness does not know about
    /// fails every scenario at once rather than quietly matching a prefix of the line.
    /// </remarks>
    /// <param name="template">The framework's own message template.</param>
    /// <param name="groups">Placeholder (as written in the template) to the sub-pattern replacing it.</param>
    /// <returns>A pattern matching the rendered line.</returns>
    internal static string PatternFrom(string template, params (string Placeholder, string Pattern)[] groups)
    {
        string pattern = Regex.Escape(template);

        foreach ((string placeholder, string replacement) in groups)
            pattern = pattern.Replace(Regex.Escape(placeholder), replacement);

        return pattern;
    }

    /// <summary>Listener on the framework's single <see cref="ActivitySource"/>.</summary>
    private readonly ActivityListener listener;

    /// <summary>Closed <c>morgana.agent</c> spans, per conversation, in completion order.</summary>
    private readonly ConcurrentDictionary<string, List<AgentSpan>> agentSpans = new ConcurrentDictionary<string, List<AgentSpan>>();

    /// <summary>Closed <c>morgana.guard</c> spans, per conversation, in completion order.</summary>
    private readonly ConcurrentDictionary<string, List<GuardSpan>> guardSpans = new ConcurrentDictionary<string, List<GuardSpan>>();

    /// <summary>Consultation spans per conversation — several may close within one turn.</summary>
    private readonly ConcurrentDictionary<string, List<ConsultationObservation>> consultationSpans = new ConcurrentDictionary<string, List<ConsultationObservation>>();

    /// <summary>Closed <c>morgana.classifier</c> spans, per conversation, in completion order.</summary>
    private readonly ConcurrentDictionary<string, List<ClassifierSpan>> classifierSpans = new ConcurrentDictionary<string, List<ClassifierSpan>>();

    /// <summary>
    /// Every closed LLM span, in completion order: its token usage and what the model read and wrote. Not
    /// keyed by conversation: the MEAI spans carry <c>gen_ai.*</c> attributes and no conversation id, so
    /// they are attributed to a turn by position in this list — sound for the same reason the log
    /// correlation is and no more.
    /// </summary>
    private readonly List<ModelCall> llmSpans = [];

    /// <summary>Guards <see cref="llmSpans"/>.</summary>
    private readonly Lock llmGate = new Lock();

    /// <summary>Tee on the host's stdout.</summary>
    private readonly HostOutputCapture output;

    /// <summary>Milliseconds to let the console logger's background queue drain before reading.</summary>
    private readonly int logDrainMilliseconds;

    /// <summary>Starts listening. Must be constructed before the first turn, not before the host.</summary>
    public TurnObserver(HostOutputCapture output, int logDrainMilliseconds)
    {
        this.output = output;
        this.logDrainMilliseconds = logDrainMilliseconds;

        listener = new ActivityListener
        {
            // "Morgana" carries the pipeline spans; "Morgana.AI.LLM" is the MEAI decorator every
            // provider is wrapped in and is where token usage lives.
            ShouldListenTo = source => source.Name is "Morgana" or "Morgana.AI.LLM",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = OnActivityStopped
        };

        ActivitySource.AddActivityListener(listener);
    }

    /// <summary>Opens an observation window for a turn about to be sent.</summary>
    public TurnScope BeginTurn(string conversationId)
    {
        // Snapshot the count, not a reference into the list: the list keeps growing as this turn's
        // LLM calls complete, so "how many entries existed before this turn" has to be captured now
        // and compared against later, in CompleteTurnAsync, rather than watched live.
        int llmMark;
        lock (llmGate)
            llmMark = llmSpans.Count;

        // Five independent marks — log line count, agent/guard/classifier span counts for this
        // conversation and global LLM-span count — each a watermark that CompleteTurnAsync will
        // read everything *past* to isolate what happened during this one turn specifically.
        return new TurnScope(
            conversationId,
            output.Mark(),
            SpanCount(agentSpans, conversationId),
            SpanCount(guardSpans, conversationId),
            SpanCount(classifierSpans, conversationId),
            llmMark,
            SpanCount(consultationSpans, conversationId));
    }

    /// <summary>
    /// Current position in the captured log — the counterpart to <see cref="TurnScope.LogMark"/> but
    /// takeable once, at conversation start, for callers that need a cumulative window spanning
    /// every turn since (e.g. a one-shot signal like a dust-budget threshold, which a single turn's
    /// own window can miss entirely depending on which turn happens to cross it — see
    /// <c>ScenarioRunner</c>'s use of this for <c>TurnResult.CumulativeLogLines</c>).
    /// </summary>
    public int Mark() => output.Mark();

    /// <summary>Closes the window and assembles what was observed alongside the delivered message.</summary>
    /// <param name="conversationLogMark">
    /// When given, <see cref="TurnResult.CumulativeLogLines"/> is populated with every log line
    /// since this mark (conversation start) rather than left empty — see <see cref="Mark"/>.
    /// </param>
    public async Task<TurnResult> CompleteTurnAsync(TurnScope scope, string userMessage, ChannelMessage message, int? conversationLogMark = null)
    {
        // The console logger writes on a background queue, so the last tool lines of a turn can
        // still be in flight when the webhook has already been delivered.
        await Task.Delay(logDrainMilliseconds);

        // Everything logged since BeginTurn's mark — this is the raw material both the context-
        // access parsing below and, unparsed, TurnResult.LogLines (used for failure diagnosis) work from.
        IReadOnlyList<string> lines = output.Since(scope.LogMark);

        // Parse every context-access log line in this window into a structured access. Lines that
        // don't match the pattern (ordinary framework noise) are silently skipped rather than
        // treated as an error — this parser only cares about the subset it recognises.
        List<ContextAccess> accesses = [];
        foreach (string line in lines)
        {
            Match match = ContextAccessPattern.Match(line);
            if (match.Success)
            {
                ContextOperation operation = match.Groups["op"].Value switch
                {
                    Constants.ObservableLogs.Hit => ContextOperation.Hit,
                    Constants.ObservableLogs.Miss => ContextOperation.Miss,
                    _ => ContextOperation.Set
                };

                accesses.Add(new ContextAccess(operation, match.Groups["name"].Value));
            }
        }

        // The latest span of each kind recorded since the turn began: spans from earlier turns of the
        // same conversation sit below the mark. Guard and classifier are null on a turn that skipped
        // that step (a follow-up skips classification, a guard-disabled run has no guard span).
        AgentSpan? span = LatestSpanSince(agentSpans, scope.ConversationId, scope.SpanCount);
        GuardSpan? guard = LatestSpanSince(guardSpans, scope.ConversationId, scope.GuardSpanCount);
        ClassifierSpan? classifier = LatestSpanSince(classifierSpans, scope.ConversationId, scope.ClassifierSpanCount);

        // Every consultation that closed during the turn, not just the latest: one turn may ask
        // more than one colleague and which of them was asked is the whole point of reading these.
        IReadOnlyList<ConsultationObservation> consulted =
            consultationSpans.TryGetValue(scope.ConversationId, out List<ConsultationObservation>? served) && served.Count > scope.ConsultationSpanCount
                ? [.. served.Skip(scope.ConsultationSpanCount)]
                : [];

        // LLM spans are process-wide, not per-conversation (see the field's own remarks on why),
        // so isolating this turn's usage means skipping every span that existed before BeginTurn's
        // mark and summing whatever landed after — sound only because the suite runs serially.
        List<ModelCall> modelCalls;
        lock (llmGate)
            modelCalls = [.. llmSpans.Skip(scope.LlmSpanCount)];
        TokenUsage usage = modelCalls.Aggregate(TokenUsage.Zero, (total, next) => total + next.Usage);

        return new TurnResult(
            scope.ConversationId,
            userMessage,
            message,
            span?.ToolsInvoked ?? [],
            accesses,
            span?.AgentName,
            usage,
            lines,
            guard?.Compliant,
            guard?.Violation,
            classifier?.Intent,
            classifier?.Confidence,
            consulted,
            conversationLogMark is { } mark ? output.Since(mark) : [],
            modelCalls);
    }

    /// <inheritdoc />
    public void Dispose() => listener.Dispose();

    /// <summary>Records a closed agent span against its conversation, or an LLM span's token usage.</summary>
    private void OnActivityStopped(Activity activity)
    {
        // This callback fires for every activity from either listened-to source (see the
        // constructor), so the first job is figuring out which kind just closed and routing
        // accordingly — the two branches below populate entirely different collections.
        if (activity.Source.Name == "Morgana.AI.LLM")
        {
            // Only a chat span is a round trip to the model. The same source also closes a span per tool
            // executed and one per tool loop that sums the usage of its own chat calls: counted, they would
            // inflate the calls and bill the loop's tokens twice.
            string operation = activity.GetTagItem("gen_ai.operation.name") as string ?? activity.OperationName;
            if (!string.Equals(operation, "chat", StringComparison.Ordinal))
                return;

            // One MEAI-decorated LLM call just completed; its gen_ai.* tags carry token usage per
            // the OpenTelemetry semantic conventions. Recorded with Calls: 1 so that summing a
            // list of these later also yields the call count, not just token totals.
            TokenUsage usage = new TokenUsage(
                ReadTokenTag(activity, "gen_ai.usage.input_tokens"),
                ReadTokenTag(activity, "gen_ai.usage.output_tokens"),
                ReadTokenTag(activity, "gen_ai.usage.cache_read.input_tokens"),
                ReadTokenTag(activity, "gen_ai.usage.cache_write.input_tokens"),
                Calls: 1);

            // The content tags are there because the fixture turns sensitive data on for the host: what the
            // model was handed and what it answered is the evidence a failing run is diagnosed from.
            ModelCall call = new ModelCall(
                usage,
                activity.GetTagItem("gen_ai.system_instructions") as string,
                activity.GetTagItem("gen_ai.tool.definitions") as string,
                activity.GetTagItem("gen_ai.input.messages") as string,
                activity.GetTagItem("gen_ai.output.messages") as string);

            lock (llmGate)
                llmSpans.Add(call);

            return;
        }

        // No conversation id tag means this span cannot be attributed to any conversation's
        // history — nothing useful to record, so it's dropped rather than filed under a null key.
        if (activity.GetTagItem(Telemetry.ConversationId) is not string conversationId)
            return;

        switch (activity.OperationName)
        {
            case Telemetry.AgentActivity:
                Append(agentSpans, conversationId, new AgentSpan(
                    activity.GetTagItem(Telemetry.AgentName) as string,
                    ReadToolsInvoked(activity)));
                break;

            case Telemetry.ConsultationActivity:
                // The tools tag is set on the answering agent's side: this is the only place the
                // harness can see which tools the colleague reached for.
                Append(consultationSpans, conversationId, new ConsultationObservation(
                    activity.GetTagItem(Telemetry.ConsultationCaller) as string,
                    activity.GetTagItem(Telemetry.ConsultationTarget) as string,
                    ReadToolsInvoked(activity),
                    activity.GetTagItem(Telemetry.ConsultationAwaitingReply) as bool?,
                    activity.GetTagItem(Telemetry.ConsultationQuestion) as string,
                    activity.GetTagItem(Telemetry.ConsultationAnswer) as string));
                break;

            case Telemetry.GuardActivity:
                Append(guardSpans, conversationId, new GuardSpan(
                    activity.GetTagItem(Telemetry.GuardCompliant) as bool?,
                    activity.GetTagItem(Telemetry.GuardViolation) as string));
                break;

            case Telemetry.ClassifierActivity:
                // classification.confidence is stored as a string tag (ConversationSupervisorActor
                // sets it from a Dictionary<string,string>), so it's parsed tolerantly here rather
                // than cast — an unparseable value becomes "unknown", not a listener crash.
                double? confidence = activity.GetTagItem(Telemetry.ClassificationConfidence) is string raw
                    && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
                        ? parsed
                        : null;

                Append(classifierSpans, conversationId, new ClassifierSpan(
                    activity.GetTagItem(Telemetry.ClassificationIntent) as string,
                    confidence));
                break;

            // Any other activity on the "Morgana" source that isn't one of the three turn-level
            // spans this observer cares about (e.g. a finer-grained internal span) is not relevant.
        }
    }

    /// <summary>
    /// Appends one closed span to its conversation's list, creating the list on first contact.
    /// Shared by all three span kinds above — each just supplies its own dictionary and record.
    /// </summary>
    private static void Append<T>(ConcurrentDictionary<string, List<T>> spans, string conversationId, T span)
        => spans.AddOrUpdate(conversationId, _ => [span], (_, existing) =>
        {
            lock (existing)
                existing.Add(span);

            return existing;
        });

    /// <summary>How many spans a conversation has in one of the per-conversation dictionaries, zero when it has none.</summary>
    private static int SpanCount<T>(ConcurrentDictionary<string, List<T>> spans, string conversationId)
        => spans.TryGetValue(conversationId, out List<T>? conversationSpans) ? conversationSpans.Count : 0;

    /// <summary>The latest span a conversation recorded after the turn's mark, or null when the turn recorded none.</summary>
    private static T? LatestSpanSince<T>(ConcurrentDictionary<string, List<T>> spans, string conversationId, int mark)
        where T : class
        => spans.TryGetValue(conversationId, out List<T>? conversationSpans) && conversationSpans.Count > mark
            ? conversationSpans[^1]
            : null;

    /// <summary>
    /// Splits the comma-joined tools tag back into the ordered list of tools the agent called. Order is
    /// kept because the <c>toolsCalledFirst</c> assertion depends on invocation order.
    /// </summary>
    private static IReadOnlyList<string> ReadToolsInvoked(Activity activity)
        => [.. (activity.GetTagItem(Telemetry.AgentToolsInvoked) as string ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    /// <summary>
    /// Reads a token count that providers report as any integral type, or as a string on some
    /// paths. Missing or unparseable means zero: a token count is a measurement, never an
    /// assertion and it must not be able to fail a scenario.
    /// </summary>
    private static long ReadTokenTag(Activity activity, string tag)
        => activity.GetTagItem(tag) switch
        {
            long value => value,
            int value => value,
            double value => (long)value,
            string text when long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) => parsed,
            _ => 0
        };

    /// <summary>What a closed <c>morgana.agent</c> span contributes to a turn result.</summary>
    private sealed record AgentSpan(string? AgentName, IReadOnlyList<string> ToolsInvoked);

    /// <summary>What a closed <c>morgana.guard</c> span contributes to a turn result.</summary>
    private sealed record GuardSpan(bool? Compliant, string? Violation);

    /// <summary>What a closed <c>morgana.classifier</c> span contributes to a turn result.</summary>
    private sealed record ClassifierSpan(string? Intent, double? Confidence);
}
