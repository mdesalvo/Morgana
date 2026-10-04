using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Morgana.AI;
using Morgana.AI.Telemetry;
using Morgana.Contracts;

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
public sealed class TurnObserver : IDisposable
{
    /// <summary>
    /// Matches the context-tool log lines emitted by <c>MorganaTool</c>, built from the framework's
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
    /// Matches <c>MorganaAIContextProvider</c>'s per-turn declaration line — the proof that one or
    /// more variables were handed to the model directly, without a <c>GetContextVariable</c> call.
    /// The line is only ever logged when the session holds at least one variable, so an empty
    /// session simply produces no match here — nothing to skip specially.
    /// </summary>
    private static readonly Regex DeclaredContextPattern = new Regex(
        PatternFrom(
            Constants.ObservableLogs.DeclaredContext,
            ("{MorganaAiContextProviderName}", Regex.Escape(Constants.ObservableLogs.ContextProviderName)),
            ("{VariableNames}", "(?<names>[^']*)")),
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
    private static string PatternFrom(string template, params (string Placeholder, string Pattern)[] groups)
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

    /// <summary>Tool results the guard screened, per conversation — one turn may screen several.</summary>
    private readonly ConcurrentDictionary<string, List<ToolGuardObservation>> toolGuardSpans = new ConcurrentDictionary<string, List<ToolGuardObservation>>();

    /// <summary>Calls to the presentation tools, per conversation — one turn may set both quick replies and a card.</summary>
    private readonly ConcurrentDictionary<string, List<PresentationObservation>> presentationSpans = new ConcurrentDictionary<string, List<PresentationObservation>>();

    /// <summary>
    /// Partners' questions the peer guard judged, per conversation. Read by conversation rather than by
    /// turn: a partner's exchange runs on no turn of the harness's own channel.
    /// </summary>
    private readonly ConcurrentDictionary<string, List<PeerGuardObservation>> peerGuardSpans = new ConcurrentDictionary<string, List<PeerGuardObservation>>();

    /// <summary>Closed <c>morgana.classifier</c> spans, per conversation, in completion order.</summary>
    private readonly ConcurrentDictionary<string, List<ClassifierSpan>> classifierSpans = new ConcurrentDictionary<string, List<ClassifierSpan>>();

    /// <summary>
    /// Every closed LLM span, in completion order. Not keyed by conversation: the MEAI spans carry
    /// <c>gen_ai.*</c> attributes and no conversation id, so they are attributed to a turn by
    /// position in this list — sound for the same reason the log correlation is and no more.
    /// </summary>
    private readonly List<LlmCallObservation> llmSpans = [];

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

        // One watermark per signal — the log, each span kind of this conversation and the global
        // LLM-span count — which CompleteTurnAsync reads everything *past* to isolate this one turn.
        return new TurnScope(
            conversationId,
            output.Mark(),
            agentSpans.TryGetValue(conversationId, out List<AgentSpan>? spans) ? spans.Count : 0,
            guardSpans.TryGetValue(conversationId, out List<GuardSpan>? guards) ? guards.Count : 0,
            classifierSpans.TryGetValue(conversationId, out List<ClassifierSpan>? classifiers) ? classifiers.Count : 0,
            llmMark,
            consultationSpans.TryGetValue(conversationId, out List<ConsultationObservation>? consultations) ? consultations.Count : 0,
            toolGuardSpans.TryGetValue(conversationId, out List<ToolGuardObservation>? screened) ? screened.Count : 0,
            presentationSpans.TryGetValue(conversationId, out List<PresentationObservation>? presented) ? presented.Count : 0);
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
                    Constants.ObservableLogs.Hit => ContextOperation.Hit,
                    Constants.ObservableLogs.Miss => ContextOperation.Miss,
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

        // Every consultation that closed during the turn, not just the latest: one turn may ask
        // more than one colleague and which of them was asked is the whole point of reading these.
        IReadOnlyList<ConsultationObservation> consulted =
            consultationSpans.TryGetValue(scope.ConversationId, out List<ConsultationObservation>? served) && served.Count > scope.ConsultationSpanCount
                ? [.. served.Skip(scope.ConsultationSpanCount)]
                : [];

        // Every result screened during the turn: which of them the guard quarantined is the whole question.
        IReadOnlyList<ToolGuardObservation> screened =
            toolGuardSpans.TryGetValue(scope.ConversationId, out List<ToolGuardObservation>? guarded) && guarded.Count > scope.ToolGuardSpanCount
                ? [.. guarded.Skip(scope.ToolGuardSpanCount)]
                : [];

        // Every presentation call of the turn: a call listed among the tools may still have been refused.
        IReadOnlyList<PresentationObservation> presentations =
            presentationSpans.TryGetValue(scope.ConversationId, out List<PresentationObservation>? presented) && presented.Count > scope.PresentationSpanCount
                ? [.. presented.Skip(scope.PresentationSpanCount)]
                : [];

        // LLM spans are process-wide, not per-conversation (see the field's own remarks on why),
        // so isolating this turn's usage means skipping every span that existed before BeginTurn's
        // mark and summing whatever landed after — sound only because the suite runs serially.
        IReadOnlyList<LlmCallObservation> calls = LlmCallsSince(scope);
        TokenUsage usage = calls.Where(call => call.IsModelCall).Aggregate(TokenUsage.Zero, (total, next) => total + next.Usage);

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
            screened,
            guard?.Source,
            presentations,
            calls);
    }

    /// <summary>
    /// What a turn that never completed did before it was abandoned — every model call and host log
    /// line since its scope was opened. A timeout is the failure whose cause is invisible from the
    /// channel, so its report must carry the same evidence a completed turn's does.
    /// </summary>
    public string BackstageSince(TurnScope scope)
        => TurnResult.RenderBackstage(LlmCallsSince(scope), output.Since(scope.LogMark));

    private IReadOnlyList<LlmCallObservation> LlmCallsSince(TurnScope scope)
    {
        lock (llmGate)
            return [.. llmSpans.Skip(scope.LlmSpanCount)];
    }

    /// <summary>
    /// Every verdict the peer guard reached on the conversation a partner's exchange is served on, in
    /// the order the spans closed.
    /// </summary>
    /// <param name="conversationId">The conversation as this installation names it, the partner's issuer included.</param>
    public IReadOnlyList<PeerGuardObservation> PeerGuardVerdicts(string conversationId)
    {
        if (!peerGuardSpans.TryGetValue(conversationId, out List<PeerGuardObservation>? verdicts))
            return [];

        lock (verdicts)
            return [.. verdicts];
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

            // What the model emitted reaches the span only because the fixture turns sensitive data
            // on for this in-process host: it is what tells a model that stopped without a word
            // from one whose words were lost on the way to the user.
            LlmCallObservation call = new LlmCallObservation(
                activity.OperationName,
                usage,
                activity.GetTagItem("gen_ai.response.model") as string ?? activity.GetTagItem("gen_ai.request.model") as string,
                ReadFinishReasons(activity.GetTagItem("gen_ai.response.finish_reasons")),
                DescribeModelOutput(activity.GetTagItem("gen_ai.output.messages") as string));

            lock (llmGate)
                llmSpans.Add(call);

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

            case MorganaTelemetry.ConsultationActivity:
                // Same comma-joined tag the agent span carries, set on the answering agent's side:
                // this is the only place the harness can see which tools the colleague reached for.
                string consultationTools = activity.GetTagItem(MorganaTelemetry.AgentToolsInvoked) as string ?? string.Empty;

                Append(consultationSpans, conversationId, new ConsultationObservation(
                    activity.GetTagItem(MorganaTelemetry.ConsultationCaller) as string,
                    activity.GetTagItem(MorganaTelemetry.ConsultationTarget) as string,
                    [.. consultationTools.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)],
                    activity.GetTagItem(MorganaTelemetry.ConsultationAwaitingReply) as bool?,
                    activity.GetTagItem(MorganaTelemetry.ConsultationQuestion) as string,
                    activity.GetTagItem(MorganaTelemetry.ConsultationAnswer) as string));
                break;

            case MorganaTelemetry.ToolGuardActivity:
                Append(toolGuardSpans, conversationId, new ToolGuardObservation(
                    activity.GetTagItem(MorganaTelemetry.ToolGuardTool) as string,
                    activity.GetTagItem(MorganaTelemetry.ToolGuardExternal) as bool?,
                    activity.GetTagItem(MorganaTelemetry.ToolGuardCompliant) as bool?,
                    activity.GetTagItem(MorganaTelemetry.ToolGuardSource) as string,
                    activity.GetTagItem(MorganaTelemetry.ToolGuardViolation) as string));
                break;

            case MorganaTelemetry.PresentationActivity:
                Append(presentationSpans, conversationId, new PresentationObservation(
                    activity.GetTagItem(MorganaTelemetry.PresentationTool) as string,
                    activity.GetTagItem(MorganaTelemetry.PresentationAccepted) as bool?,
                    activity.GetTagItem(MorganaTelemetry.PresentationRejection) as string));
                break;

            case MorganaTelemetry.PeerGuardActivity:
                Append(peerGuardSpans, conversationId, new PeerGuardObservation(
                    activity.GetTagItem(MorganaTelemetry.PeerGuardCaller) as string,
                    activity.GetTagItem(MorganaTelemetry.PeerGuardTarget) as string,
                    activity.GetTagItem(MorganaTelemetry.PeerGuardCompliant) as bool?,
                    activity.GetTagItem(MorganaTelemetry.PeerGuardSource) as string,
                    activity.GetTagItem(MorganaTelemetry.PeerGuardViolation) as string));
                break;

            case MorganaTelemetry.GuardActivity:
                Append(guardSpans, conversationId, new GuardSpan(
                    activity.GetTagItem(MorganaTelemetry.GuardCompliant) as bool?,
                    activity.GetTagItem(MorganaTelemetry.GuardViolation) as string,
                    activity.GetTagItem(MorganaTelemetry.GuardSource) as string));
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

    // The provider's reasons arrive as one array per call; a call reports one reason in practice.
    private static string? ReadFinishReasons(object? tag) => tag switch
    {
        string[] reasons => string.Join(",", reasons),
        string text => text.Trim('[', ']').Replace("\"", "", StringComparison.Ordinal),
        _ => null
    };

    /// <summary>
    /// Turns the <c>gen_ai.output.messages</c> document into one entry per emitted part: a text with
    /// its length and opening, a tool call with its name and arguments. An empty text is named as
    /// such, because a turn that ends on one is exactly the case this exists to expose.
    /// </summary>
    private static IReadOnlyList<string> DescribeModelOutput(string? outputMessages)
    {
        if (string.IsNullOrEmpty(outputMessages))
            return [];

        List<string> parts = [];
        try
        {
            using JsonDocument document = JsonDocument.Parse(outputMessages);
            foreach (JsonElement message in document.RootElement.EnumerateArray())
            {
                if (!message.TryGetProperty("parts", out JsonElement messageParts))
                    continue;

                foreach (JsonElement part in messageParts.EnumerateArray())
                    parts.Add(DescribeModelOutputPart(part));
            }
        }
        catch (JsonException)
        {
            // A shape this reader does not know is still evidence: shown raw rather than dropped.
            parts.Add($"unparsed output: {Excerpt(outputMessages, 400)}");
        }

        return parts;
    }

    private static string DescribeModelOutputPart(JsonElement part)
    {
        string type = part.TryGetProperty("type", out JsonElement typeElement) ? typeElement.GetString() ?? "?" : "?";
        if (type == "tool_call")
        {
            string name = part.TryGetProperty("name", out JsonElement nameElement) ? nameElement.GetString() ?? "?" : "?";
            string arguments = part.TryGetProperty("arguments", out JsonElement argumentsElement) ? argumentsElement.GetRawText() : "";
            return $"call {name}({Excerpt(arguments, 300)})";
        }

        // MEAI writes a standard part with its content as a string, but any content it has no
        // convention for as a serialized object named by its .NET type: reasoning arrives that way,
        // so the type is always shown, short, lest a thought read as an answer.
        string kind = type[(type.LastIndexOf('.') + 1)..];
        string? text = part.TryGetProperty("content", out JsonElement content)
            ? content.ValueKind == JsonValueKind.String
                ? content.GetString()
                : content.ValueKind == JsonValueKind.Object && content.TryGetProperty("text", out JsonElement nestedText) ? nestedText.GetString() : null
            : null;

        if (text is null)
            return kind;

        return text.Length == 0 ? $"{kind} (empty)" : $"{kind}[{text.Length}] \"{Excerpt(text, 200)}\"";
    }

    private static string Excerpt(string value, int length)
    {
        string singleLine = value.ReplaceLineEndings(" ");
        return singleLine.Length <= length ? singleLine : singleLine[..length] + "…";
    }

    /// <summary>What a closed <c>morgana.agent</c> span contributes to a turn result.</summary>
    private sealed record AgentSpan(string? AgentName, IReadOnlyList<string> ToolsInvoked);

    /// <summary>What a closed <c>morgana.guard</c> span contributes to a turn result.</summary>
    private sealed record GuardSpan(bool? Compliant, string? Violation, string? Source);

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
/// <param name="ToolGuardSpanCount">Screened tool results already recorded for the conversation.</param>
/// <param name="PresentationSpanCount">Presentation tool calls already recorded for the conversation.</param>
public sealed record TurnScope(string ConversationId, int LogMark, int SpanCount, int GuardSpanCount, int ClassifierSpanCount, int LlmSpanCount, int ConsultationSpanCount, int ToolGuardSpanCount, int PresentationSpanCount);
