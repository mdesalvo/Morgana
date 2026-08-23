using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Morgana.Contracts;
using Morgana.AI.Telemetry;

namespace PromptHarness.Infrastructure.Wiring;

/// <summary>
/// Reads the two structural signals a turn leaves behind inside the host process: the
/// <c>morgana.agent</c> span and the context-related log lines — from <c>MorganaTool</c>
/// (HIT/MISS/SET) and from <c>MorganaAIContextProvider</c>'s per-turn declaration.
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
public sealed partial class TurnObserver : IDisposable
{
    /// <summary>Matches the context-tool log lines emitted by <c>MorganaTool</c>.</summary>
    [GeneratedRegex(@"MorganaTool \([^)]*\) (?<op>HIT|MISS|SET) variable '(?<name>[^']*)'", RegexOptions.CultureInvariant)]
    private static partial Regex ContextAccessPattern { get; }

    /// <summary>
    /// Matches <c>MorganaAIContextProvider</c>'s per-turn declaration line — the proof that one or
    /// more variables were handed to the model directly, without a <c>GetContextVariable</c> call.
    /// The line is only ever logged when the session holds at least one variable, so an empty
    /// session simply produces no match here — nothing to skip specially.
    /// </summary>
    [GeneratedRegex(@"MorganaAIContextProvider DECLARED '(?<names>[^']*)'", RegexOptions.CultureInvariant)]
    private static partial Regex DeclaredContextPattern { get; }

    /// <summary>Listener on the framework's single <see cref="ActivitySource"/>.</summary>
    private readonly ActivityListener listener;

    /// <summary>Closed <c>morgana.agent</c> spans, per conversation, in completion order.</summary>
    private readonly ConcurrentDictionary<string, List<AgentSpan>> agentSpans = new ConcurrentDictionary<string, List<AgentSpan>>();

    /// <summary>Closed <c>morgana.guard</c> spans, per conversation, in completion order.</summary>
    private readonly ConcurrentDictionary<string, List<GuardSpan>> guardSpans = new ConcurrentDictionary<string, List<GuardSpan>>();

    /// <summary>Closed <c>morgana.classifier</c> spans, per conversation, in completion order.</summary>
    private readonly ConcurrentDictionary<string, List<ClassifierSpan>> classifierSpans = new ConcurrentDictionary<string, List<ClassifierSpan>>();

    /// <summary>
    /// Token usage of every closed LLM span, in completion order. Not keyed by conversation: the
    /// MEAI spans carry <c>gen_ai.*</c> attributes and no conversation id, so they are attributed
    /// to a turn by position in this list — sound for the same reason the log correlation is, and
    /// no more.
    /// </summary>
    private readonly List<TokenUsage> llmSpans = [];

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
            // provider is wrapped in, and is where token usage lives.
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
        // conversation, and global LLM-span count — each a watermark that CompleteTurnAsync will
        // read everything *past* to isolate what happened during this one turn specifically.
        return new TurnScope(
            conversationId,
            output.Mark(),
            agentSpans.TryGetValue(conversationId, out List<AgentSpan>? spans) ? spans.Count : 0,
            guardSpans.TryGetValue(conversationId, out List<GuardSpan>? guards) ? guards.Count : 0,
            classifierSpans.TryGetValue(conversationId, out List<ClassifierSpan>? classifiers) ? classifiers.Count : 0,
            llmMark);
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

        // Parse every context-tool log line in this window into a structured access. Lines that
        // don't match either pattern (ordinary framework noise) are silently skipped rather than
        // treated as an error — this parser only cares about the subset it recognises.
        List<ContextAccess> accesses = [];
        foreach (string line in lines)
        {
            Match match = ContextAccessPattern.Match(line);
            if (match.Success)
            {
                ContextOperation operation = match.Groups["op"].Value switch
                {
                    "HIT" => ContextOperation.Hit,
                    "MISS" => ContextOperation.Miss,
                    _ => ContextOperation.Set
                };

                accesses.Add(new ContextAccess(operation, match.Groups["name"].Value));
                continue;
            }

            // One DECLARED line can name several variables at once ("customerCode, invoiceId") —
            // one ContextAccess per name, same as a tool-log line would produce one per call.
            Match declared = DeclaredContextPattern.Match(line);
            if (declared.Success)
            {
                foreach (string name in declared.Groups["names"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    accesses.Add(new ContextAccess(ContextOperation.Declared, name));
            }
        }

        // The most recent agent span recorded for this conversation since the turn began, if any —
        // spans.Count > scope.SpanCount is what filters out spans from earlier turns of the same
        // conversation; [^1] takes the latest of whatever new ones landed during this turn.
        AgentSpan? span = agentSpans.TryGetValue(scope.ConversationId, out List<AgentSpan>? spans) && spans.Count > scope.SpanCount
            ? spans[^1]
            : null;

        // Same "count > mark, take the latest" read as the agent span above, applied to the guard
        // and classifier dictionaries — both are null on any turn that skipped that step (e.g. a
        // follow-up turn skips classification, a guard-disabled run never produces a guard span).
        GuardSpan? guard = guardSpans.TryGetValue(scope.ConversationId, out List<GuardSpan>? guards) && guards.Count > scope.GuardSpanCount
            ? guards[^1]
            : null;

        ClassifierSpan? classifier = classifierSpans.TryGetValue(scope.ConversationId, out List<ClassifierSpan>? classifiers) && classifiers.Count > scope.ClassifierSpanCount
            ? classifiers[^1]
            : null;

        // LLM spans are process-wide, not per-conversation (see the field's own remarks on why),
        // so isolating this turn's usage means skipping every span that existed before BeginTurn's
        // mark and summing whatever landed after — sound only because the suite runs serially.
        TokenUsage usage;
        lock (llmGate)
            usage = llmSpans.Skip(scope.LlmSpanCount).Aggregate(TokenUsage.Zero, (total, next) => total + next);

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
            conversationLogMark is { } mark ? output.Since(mark) : []);
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
            // One MEAI-decorated LLM call just completed; its gen_ai.* tags carry token usage per
            // the OpenTelemetry semantic conventions. Recorded with Calls: 1 so that summing a
            // list of these later also yields the call count, not just token totals.
            TokenUsage usage = new TokenUsage(
                ReadTokenTag(activity, "gen_ai.usage.input_tokens"),
                ReadTokenTag(activity, "gen_ai.usage.output_tokens"),
                ReadTokenTag(activity, "gen_ai.usage.cache_read.input_tokens"),
                ReadTokenTag(activity, "gen_ai.usage.cache_write.input_tokens"),
                Calls: 1);

            lock (llmGate)
                llmSpans.Add(usage);

            return;
        }

        // No conversation id tag means this span cannot be attributed to any conversation's
        // history — nothing useful to record, so it's dropped rather than filed under a null key.
        if (activity.GetTagItem(MorganaTelemetry.ConversationId) is not string conversationId)
            return;

        switch (activity.OperationName)
        {
            case MorganaTelemetry.AgentActivity:
                // agent.tools_invoked is a single comma-joined string tag (spans can't carry
                // arrays), so it has to be split back into an ordered list here — order matters,
                // since ExpectationChecker's toolsCalledFirst assertion depends on invocation order.
                string toolsInvoked = activity.GetTagItem(MorganaTelemetry.AgentToolsInvoked) as string ?? string.Empty;

                Append(agentSpans, conversationId, new AgentSpan(
                    activity.GetTagItem(MorganaTelemetry.AgentName) as string,
                    [.. toolsInvoked.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]));
                break;

            case MorganaTelemetry.GuardActivity:
                Append(guardSpans, conversationId, new GuardSpan(
                    activity.GetTagItem(MorganaTelemetry.GuardCompliant) as bool?,
                    activity.GetTagItem(MorganaTelemetry.GuardViolation) as string));
                break;

            case MorganaTelemetry.ClassifierActivity:
                // classification.confidence is stored as a string tag (ConversationSupervisorActor
                // sets it from a Dictionary<string,string>), so it's parsed tolerantly here rather
                // than cast — an unparseable value becomes "unknown", not a listener crash.
                double? confidence = activity.GetTagItem(MorganaTelemetry.ClassificationConfidence) is string raw
                    && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
                        ? parsed
                        : null;

                Append(classifierSpans, conversationId, new ClassifierSpan(
                    activity.GetTagItem(MorganaTelemetry.ClassificationIntent) as string,
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

    /// <summary>
    /// Reads a token count that providers report as any integral type, or as a string on some
    /// paths. Missing or unparseable means zero: a token count is a measurement, never an
    /// assertion, and it must not be able to fail a scenario.
    /// </summary>
    private static long ReadTokenTag(Activity activity, string tag)
        => activity.GetTagItem(tag) switch
        {
            long value => value,
            int value => value,
            double value => (long)value,
            string text when long.TryParse(text, out long parsed) => parsed,
            _ => 0
        };

    /// <summary>What a closed <c>morgana.agent</c> span contributes to a turn result.</summary>
    private sealed record AgentSpan(string? AgentName, IReadOnlyList<string> ToolsInvoked);

    /// <summary>What a closed <c>morgana.guard</c> span contributes to a turn result.</summary>
    private sealed record GuardSpan(bool? Compliant, string? Violation);

    /// <summary>What a closed <c>morgana.classifier</c> span contributes to a turn result.</summary>
    private sealed record ClassifierSpan(string? Intent, double? Confidence);
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
    /// <summary>The empty measurement, and the seed of every aggregation.</summary>
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

/// <summary>
/// Marks the start of a turn's observation window: where the log stood, and how many agent, guard
/// and classifier spans the conversation had already produced.
/// </summary>
/// <param name="ConversationId">Conversation being observed.</param>
/// <param name="LogMark">Index into the captured log at the moment the turn was sent.</param>
/// <param name="SpanCount">Agent spans already recorded for the conversation.</param>
/// <param name="GuardSpanCount">Guard spans already recorded for the conversation.</param>
/// <param name="ClassifierSpanCount">Classifier spans already recorded for the conversation.</param>
/// <param name="LlmSpanCount">LLM spans already recorded, process-wide.</param>
public sealed record TurnScope(string ConversationId, int LogMark, int SpanCount, int GuardSpanCount, int ClassifierSpanCount, int LlmSpanCount);
