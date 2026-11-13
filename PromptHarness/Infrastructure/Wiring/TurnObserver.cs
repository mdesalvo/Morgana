using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text.RegularExpressions;
using Morgana.AI;
using Morgana.Contracts;

namespace PromptHarness.Infrastructure.Wiring;

/// <summary>
/// Reads the signals a turn leaves inside the host process: Morgana's spans and security counter and the context log lines of <c>MorganaTool</c> and <c>MorganaAIContextProvider</c>.
/// All of them are production instrumentation the suite only listens to, since an assertion needing a hook of its own would measure the rig;
/// the listener also makes the sources create real activities with every exporter off, so no collector is needed.
/// </summary>
public sealed class TurnObserver : IDisposable
{
    /// <summary>
    /// Matches the context-access log lines of <c>MorganaTool</c>, built from the framework's own message template rather than from a copy.
    /// They are the only place a variable's name becomes observable, so a reworded line must break here in the same commit, never pass silently.
    /// </summary>
    private static readonly Regex ContextAccessPattern = new Regex(
        PatternFrom(
            Constants.ObservableLogs.ContextAccessHead,
            ("{MorganaToolName}", Regex.Escape(Constants.ObservableLogs.ToolName)),
            ("{Name}", "[^)]*"),
            ("{Operation}", $"(?<op>{Constants.ObservableLogs.Hit}|{Constants.ObservableLogs.Miss}|{Constants.ObservableLogs.Set})"),
            ("{VariableName}", "(?<name>[^']*)")),
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Matches <c>MorganaAIContextProvider</c>'s per-turn declaration line, the proof that variables were handed to the model without a <c>GetContextVariable</c> call.
    /// The line is logged only when the session holds a variable, so an empty session simply produces no match.
    /// </summary>
    private static readonly Regex DeclaredContextPattern = new Regex(
        PatternFrom(
            Constants.ObservableLogs.DeclaredContext,
            ("{MorganaAiContextProviderName}", Regex.Escape(Constants.ObservableLogs.ContextProviderName)),
            ("{VariableNames}", "(?<names>[^']*)")),
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Turns a logging message template into a regex: the text matched literally, each named placeholder replaced by what the caller reads out of it.
    /// A placeholder the caller does not name stays literal and never matches, so a template that grew a field fails every scenario at once.
    /// </summary>
    /// <param name="template">The framework's own message template.</param>
    /// <param name="groups">Placeholder (as written in the template) to the sub-pattern replacing it.</param>
    /// <returns>A pattern matching the rendered line.</returns>
    private static string PatternFrom(string template, params (string Placeholder, string Pattern)[] groups)
    {
        // Escaped first, so the parentheses around the tool's class name stay literal.
        string pattern = Regex.Escape(template);

        // Each placeholder is found in its escaped form, as the template now holds it.
        foreach ((string placeholder, string replacement) in groups)
            pattern = pattern.Replace(Regex.Escape(placeholder), replacement);

        // The pattern of the rendered line.
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
    /// Every closed LLM span in completion order, with its token usage and what the model read and wrote.
    /// The spans carry no conversation id, so a turn claims them by position, which holds only because the suite runs serially.
    /// </summary>
    private readonly List<ModelCall> llmSpans = [];

    /// <summary>Guards <see cref="llmSpans"/>.</summary>
    private readonly Lock llmGate = new Lock();

    /// <summary>Tool results the framework withheld, per conversation, in the order the events closed.</summary>
    private readonly ConcurrentDictionary<string, List<WithheldToolObservation>> withheldTools = new ConcurrentDictionary<string, List<WithheldToolObservation>>();

    /// <summary>Listener on Morgana's security counter, which counts what happens at every guarded door.</summary>
    private readonly MeterListener securityListener;

    /// <summary>Every security event counted since the process started, in the order it was counted: not keyed by conversation because the counter carries none.</summary>
    private readonly List<SecurityEventObservation> securityEvents = [];

    /// <summary>Guards <see cref="securityEvents"/>, which the counter's callers write from their own threads.</summary>
    private readonly Lock securityGate = new Lock();

    /// <summary>Tee on the host's stdout.</summary>
    private readonly HostOutputCapture output;

    /// <summary>Milliseconds to let the console logger's background queue drain before reading.</summary>
    private readonly int logDrainMilliseconds;

    /// <summary>Starts listening; it must exist before the first turn, which may come after the host started.</summary>
    public TurnObserver(HostOutputCapture output, int logDrainMilliseconds)
    {
        // The host's captured log and how long its background queue is given to drain before a turn reads it.
        this.output = output;
        this.logDrainMilliseconds = logDrainMilliseconds;

        // Every span of the two sources is recorded in full, whatever the host's own sampling says.
        listener = new ActivityListener
        {
            // "Morgana" carries the pipeline spans; "Morgana.AI.LLM" is the MEAI decorator every
            // provider is wrapped in and is where token usage lives.
            ShouldListenTo = source => source.Name is "Morgana" or "Morgana.AI.LLM",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = OnActivityStopped
        };

        // From here every span the host closes on the two sources reaches OnActivityStopped.
        ActivitySource.AddActivityListener(listener);

        // The names are read before the listener exists: reading them is what makes Telemetry create its
        // instruments. A listener already started would be told of them while that is still under way.
        string meterName = Telemetry.MorganaMeter.Name;
        string securityCounterName = Telemetry.SecurityEvents.Name;

        // Only Morgana's security counter is of interest: it is the one place that records an offer refused at startup.
        securityListener = new MeterListener
        {
            InstrumentPublished = (Instrument instrument, MeterListener meterListener) =>
            {
                // Every other instrument of the meter is left unheard.
                if (instrument.Meter.Name == meterName && instrument.Name == securityCounterName)
                    meterListener.EnableMeasurementEvents(instrument);
            }
        };

        // The counter adds whole events, so a long measurement is all this listener ever receives.
        securityListener.SetMeasurementEventCallback<long>(OnSecurityMeasurement);

        // Started after the callback is set so that no event is counted before anything can record it.
        securityListener.Start();
    }

    /// <summary>Opens an observation window for a turn about to be sent.</summary>
    public TurnScope BeginTurn(string conversationId)
    {
        // The number of model calls closed so far: the turn's own calls are the ones that close after it.
        int llmMark;
        lock (llmGate)
            llmMark = llmSpans.Count;

        // The security events are process-wide like the LLM spans, so the turn's window starts at their count.
        int securityEventMark;
        lock (securityGate)
            securityEventMark = securityEvents.Count;

        // One mark per signal, the log, each kind of span and the security events: CompleteTurnAsync reads what lies past each.
        return new TurnScope(
            conversationId,
            output.Mark(),
            agentSpans.TryGetValue(conversationId, out List<AgentSpan>? spans) ? spans.Count : 0,
            guardSpans.TryGetValue(conversationId, out List<GuardSpan>? guards) ? guards.Count : 0,
            classifierSpans.TryGetValue(conversationId, out List<ClassifierSpan>? classifiers) ? classifiers.Count : 0,
            llmMark,
            consultationSpans.TryGetValue(conversationId, out List<ConsultationObservation>? consultations) ? consultations.Count : 0,
            withheldTools.TryGetValue(conversationId, out List<WithheldToolObservation>? withheldObservations) ? withheldObservations.Count : 0,
            securityEventMark);
    }

    /// <summary>
    /// The current position in the captured log, taken once at conversation start for a window spanning every turn since.
    /// A signal sent once, such as a dust threshold, falls on a turn that varies, so <c>DustTests</c> reads it across the whole conversation.
    /// </summary>
    public int Mark() => output.Mark();

    /// <summary>How many LLM calls have closed so far: the start of a window that is not a turn's, such as the judge's.</summary>
    public int ModelCallMark()
    {
        // Read under the gate, since calls close on the provider's threads.
        lock (llmGate)
            return llmSpans.Count;
    }

    /// <summary>Every LLM call closed since <paramref name="mark"/>, in completion order.</summary>
    public IReadOnlyList<ModelCall> ModelCallsSince(int mark)
    {
        // The calls past the mark are the window's own, sound for the same serial-suite reason as a turn's.
        lock (llmGate)
            return [.. llmSpans.Skip(mark)];
    }

    /// <summary>Closes the window and assembles what was observed alongside the delivered message.</summary>
    /// <param name="conversationLogMark">A mark from <see cref="Mark"/>; given, <see cref="TurnResult.CumulativeLogLines"/> holds every log line since it.</param>
    public async Task<TurnResult> CompleteTurnAsync(TurnScope scope, string userMessage, ChannelMessage message, int? conversationLogMark = null)
    {
        // The console logger writes on a background queue, so the last tool lines of a turn can
        // still be in flight when the webhook has already been delivered.
        await Task.Delay(logDrainMilliseconds);

        // Everything logged since the turn's mark: the context accesses are parsed from it and failures print it whole.
        IReadOnlyList<string> lines = output.Since(scope.LogMark);

        // Every context line of the window becomes an access; any other line is ordinary framework output and is skipped.
        List<ContextAccess> accesses = [];
        foreach (string line in lines)
        {
            // A tool's access: one line per call.
            Match match = ContextAccessPattern.Match(line);
            if (match.Success)
            {
                // The operation word of the line, which the pattern allows to be only one of the three.
                ContextOperation operation = match.Groups["op"].Value switch
                {
                    Constants.ObservableLogs.Hit => ContextOperation.Hit,
                    Constants.ObservableLogs.Miss => ContextOperation.Miss,
                    _ => ContextOperation.Set
                };

                accesses.Add(new ContextAccess(operation, match.Groups["name"].Value));
                continue;
            }

            // One declaration line can name several variables at once, each becoming an access of its own.
            Match declared = DeclaredContextPattern.Match(line);
            if (declared.Success)
            {
                foreach (string name in declared.Groups["names"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    accesses.Add(new ContextAccess(ContextOperation.Declared, name));
            }
        }

        // The latest agent span the conversation closed since the mark; none on a turn no agent took.
        AgentSpan? span = agentSpans.TryGetValue(scope.ConversationId, out List<AgentSpan>? spans) && spans.Count > scope.SpanCount
            ? spans[^1]
            : null;

        // The latest guard span since the mark; none on a run with the guard off.
        GuardSpan? guard = guardSpans.TryGetValue(scope.ConversationId, out List<GuardSpan>? guards) && guards.Count > scope.GuardSpanCount
            ? guards[^1]
            : null;

        // The latest classifier span since the mark; none on a follow-up turn, which skips classification.
        ClassifierSpan? classifier = classifierSpans.TryGetValue(scope.ConversationId, out List<ClassifierSpan>? classifiers) && classifiers.Count > scope.ClassifierSpanCount
            ? classifiers[^1]
            : null;

        // Every consultation that closed during the turn, not just the latest: one turn may ask
        // more than one colleague and which of them was asked is the whole point of reading these.
        IReadOnlyList<ConsultationObservation> consulted =
            consultationSpans.TryGetValue(scope.ConversationId, out List<ConsultationObservation>? served) && served.Count > scope.ConsultationSpanCount
                ? [.. served.Skip(scope.ConsultationSpanCount)]
                : [];

        // The model calls closed since the mark are the turn's, which holds only because the suite runs serially.
        List<ModelCall> modelCalls;
        lock (llmGate)
            modelCalls = [.. llmSpans.Skip(scope.LlmSpanCount)];

        // Their usage summed, the call count included.
        TokenUsage usage = modelCalls.Aggregate(TokenUsage.Zero, (total, next) => total + next.Usage);

        // Every tool result withheld during the turn, with the layer that decided it: a result the
        // framework kept from the model leaves no other trace on the delivered message.
        IReadOnlyList<WithheldToolObservation> withheldDuringTurn =
            withheldTools.TryGetValue(scope.ConversationId, out List<WithheldToolObservation>? withheldList) && withheldList.Count > scope.WithheldToolCount
                ? [.. withheldList.Skip(scope.WithheldToolCount)]
                : [];

        // The security events counted since the turn began and every one counted since the process
        // started: an offer refused at startup belongs to no turn but is evidence all the same.
        List<SecurityEventObservation> securityEventsDuringTurn;
        List<SecurityEventObservation> securityEventsSinceStart;
        lock (securityGate)
        {
            securityEventsDuringTurn = [.. securityEvents.Skip(scope.SecurityEventCount)];
            securityEventsSinceStart = [.. securityEvents];
        }

        // Everything the turn's expectations, the judge and the failure log read.
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
            guard?.Source,
            guard?.ErrorType,
            classifier?.Intent,
            classifier?.Confidence,
            consulted,
            conversationLogMark is { } mark ? output.Since(mark) : [],
            modelCalls,
            WithheldToolObservations: withheldDuringTurn,
            SecurityEventWindow: securityEventsDuringTurn,
            ProcessSecurityEvents: securityEventsSinceStart);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // Both listeners stop hearing the host together.
        listener.Dispose();
        securityListener.Dispose();
    }

    /// <summary>Records one measurement of the security counter with the door it happened at and what was decided there.</summary>
    private void OnSecurityMeasurement(Instrument instrument, long measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        // The four tags a check reads; a door and an outcome are always set by the framework, a source and a cause only sometimes.
        string door = string.Empty;
        string outcome = string.Empty;
        string? source = null;
        string? errorType = null;

        // Each tag is read into its field; the others, such as the agent's intent, no check reads.
        foreach (KeyValuePair<string, object?> tag in tags)
        {
            switch (tag.Key)
            {
                case Telemetry.SecurityDoor:
                    door = tag.Value as string ?? string.Empty;
                    break;

                case Telemetry.SecurityOutcome:
                    outcome = tag.Value as string ?? string.Empty;
                    break;

                case Telemetry.SecuritySource:
                    source = tag.Value as string;
                    break;

                case Telemetry.ErrorType:
                    errorType = tag.Value as string;
                    break;
            }
        }

        // Counters of other threads write here at the same time, hence the gate.
        lock (securityGate)
            securityEvents.Add(new SecurityEventObservation(door, outcome, source, errorType));
    }

    /// <summary>Finds the conversation a span belongs to by walking up its parents until one carries the conversation id.</summary>
    private static string? FindConversationId(Activity activity)
    {
        // A child of the agent span carries no conversation tag of its own: its parent does.
        for (Activity? spanInChain = activity; spanInChain is not null; spanInChain = spanInChain.Parent)
        {
            // The first span up the chain that names a conversation decides.
            if (spanInChain.GetTagItem(Telemetry.ConversationId) is string conversationId)
                return conversationId;
        }

        // A span outside every conversation, such as one of a startup examination.
        return null;
    }

    /// <summary>Reads a string tag of an event; null when the event does not carry it.</summary>
    private static string? ReadEventTag(ActivityEvent activityEvent, string tagName)
    {
        // An event carries few tags, so they are read one by one.
        foreach (KeyValuePair<string, object?> tag in activityEvent.Tags)
        {
            // The tag asked for, read as text.
            if (tag.Key == tagName)
                return tag.Value as string;
        }

        // The event does not carry it.
        return null;
    }

    /// <summary>Records every tool-withheld event of a stopped span against the conversation it belongs to.</summary>
    private void RecordWithheldTools(Activity activity)
    {
        // A span may carry several events, one per tool withheld while it was current.
        foreach (ActivityEvent activityEvent in activity.Events)
        {
            // Only the event the framework adds when it keeps a tool result from the model is of interest.
            if (!string.Equals(activityEvent.Name, Telemetry.EventToolWithheld, StringComparison.Ordinal))
                continue;

            // An event on a span outside every conversation cannot be attributed to a turn.
            string? conversationId = FindConversationId(activity);
            if (conversationId is null)
                continue;

            // The tool, the outcome, the layer that decided and its finding, as the event carries them.
            Append(withheldTools, conversationId, new WithheldToolObservation(
                ReadEventTag(activityEvent, Telemetry.ToolName) ?? string.Empty,
                ReadEventTag(activityEvent, Telemetry.SecurityOutcome) ?? string.Empty,
                ReadEventTag(activityEvent, Telemetry.SecuritySource),
                ReadEventTag(activityEvent, Telemetry.SecurityFinding)));
        }
    }

    /// <summary>Records a closed pipeline span against its conversation and a closed model call with its token usage.</summary>
    private void OnActivityStopped(Activity activity)
    {
        // The withheld-tool event lands on whichever span was current when the tool ran, which may be
        // a tool span of the LLM source, so it is read before the routing below discards that source.
        RecordWithheldTools(activity);

        // A span of either source arrives here: a model call is recorded apart from the pipeline's spans.
        if (activity.Source.Name == "Morgana.AI.LLM")
        {
            // Only a chat span is a round trip to the model. The same source also closes a span per tool
            // executed and one per tool loop that sums the usage of its own chat calls: counted, they would
            // inflate the calls and bill the loop's tokens twice.
            string operation = activity.GetTagItem("gen_ai.operation.name") as string ?? activity.OperationName;
            if (!string.Equals(operation, "chat", StringComparison.Ordinal))
                return;

            // The usage of one call under the OpenTelemetry gen_ai.* names, counted as one call so a sum also counts the round trips.
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

            // Calls close on the provider's threads, hence the gate.
            lock (llmGate)
                llmSpans.Add(call);

            // A model call is no pipeline span.
            return;
        }

        // A pipeline span naming no conversation belongs to no turn.
        if (activity.GetTagItem(Telemetry.ConversationId) is not string conversationId)
            return;

        // Any other span of the pipeline, such as the turn's or the router's, gives a turn nothing to assert on.
        switch (activity.OperationName)
        {
            // The agent's tools travel as one comma-joined tag and are split back in call order, which toolsCalledFirst reads.
            case Telemetry.AgentActivity:
                string toolsInvoked = activity.GetTagItem(Telemetry.AgentToolsInvoked) as string ?? string.Empty;

                Append(agentSpans, conversationId, new AgentSpan(
                    activity.GetTagItem(Telemetry.AgentName) as string,
                    [.. toolsInvoked.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]));
                break;

            // The same tag on the answering side: the only place the harness sees which tools a colleague reached for.
            case Telemetry.ConsultationActivity:
                string consultationTools = activity.GetTagItem(Telemetry.AgentToolsInvoked) as string ?? string.Empty;

                Append(consultationSpans, conversationId, new ConsultationObservation(
                    activity.GetTagItem(Telemetry.ConsultationCaller) as string,
                    activity.GetTagItem(Telemetry.ConsultationTarget) as string,
                    [.. consultationTools.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)],
                    activity.GetTagItem(Telemetry.ConsultationAwaitingReply) as bool?,
                    activity.GetTagItem(Telemetry.ConsultationQuestion) as string,
                    activity.GetTagItem(Telemetry.ConsultationAnswer) as string));
                break;

            // The guard's verdict, the layer that decided and its cause when it could not judge.
            case Telemetry.GuardActivity:
                Append(guardSpans, conversationId, new GuardSpan(
                    activity.GetTagItem(Telemetry.GuardCompliant) as bool?,
                    activity.GetTagItem(Telemetry.GuardViolation) as string,
                    activity.GetTagItem(Telemetry.GuardSource) as string,
                    activity.GetTagItem(Telemetry.ErrorType) as string));
                break;

            // The confidence travels as text from the classification's metadata, so an unreadable one becomes unknown, never a crash.
            case Telemetry.ClassifierActivity:
                double? confidence = activity.GetTagItem(Telemetry.ClassificationConfidence) is string confidenceText
                    && double.TryParse(confidenceText, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedConfidence)
                        ? parsedConfidence
                        : null;

                Append(classifierSpans, conversationId, new ClassifierSpan(
                    activity.GetTagItem(Telemetry.ClassificationIntent) as string,
                    confidence));
                break;
        }
    }

    /// <summary>Appends one observation to its conversation's list, creating the list on its first observation.</summary>
    private static void Append<T>(ConcurrentDictionary<string, List<T>> spans, string conversationId, T span)
        => spans.AddOrUpdate(conversationId, _ => [span], (_, existing) =>
        {
            // Spans close on several threads at once, so the list is written under its own lock.
            lock (existing)
                existing.Add(span);

            // The same list, grown by one.
            return existing;
        });

    /// <summary>
    /// Reads a token count that providers report as any numeric type or as text; missing or unreadable means zero.
    /// A token count is a measurement, never an assertion, so it must not be able to fail a scenario.
    /// </summary>
    private static long ReadTokenTag(Activity activity, string tag)
        // Each form a provider writes maps to the count; anything else reads as no tokens.
        => activity.GetTagItem(tag) switch
        {
            long value => value,
            int value => value,
            double value => (long)value,
            string text when long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsedCount) => parsedCount,
            _ => 0
        };

    /// <summary>What a closed <c>morgana.agent</c> span contributes to a turn result.</summary>
    private sealed record AgentSpan(string? AgentName, IReadOnlyList<string> ToolsInvoked);

    /// <summary>What a closed <c>morgana.guard</c> span contributes to a turn result.</summary>
    private sealed record GuardSpan(bool? Compliant, string? Violation, string? Source, string? ErrorType);

    /// <summary>What a closed <c>morgana.classifier</c> span contributes to a turn result.</summary>
    private sealed record ClassifierSpan(string? Intent, double? Confidence);
}

/// <summary>Token usage summed over the LLM calls of a turn, the cost a prompt revision is expected to move.</summary>
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

/// <summary>Marks the start of a turn's observation window: where the log stood and how many of each signal had already been recorded.</summary>
/// <param name="ConversationId">Conversation being observed.</param>
/// <param name="LogMark">Index into the captured log at the moment the turn was sent.</param>
/// <param name="SpanCount">Agent spans already recorded for the conversation.</param>
/// <param name="GuardSpanCount">Guard spans already recorded for the conversation.</param>
/// <param name="ConsultationSpanCount">Consultation spans already recorded for the conversation.</param>
/// <param name="ClassifierSpanCount">Classifier spans already recorded for the conversation.</param>
/// <param name="LlmSpanCount">LLM spans already recorded, process-wide.</param>
/// <param name="WithheldToolCount">Withheld-tool events already recorded for the conversation.</param>
/// <param name="SecurityEventCount">Security events already counted, process-wide.</param>
public sealed record TurnScope(string ConversationId, int LogMark, int SpanCount, int GuardSpanCount, int ClassifierSpanCount, int LlmSpanCount, int ConsultationSpanCount, int WithheldToolCount, int SecurityEventCount);