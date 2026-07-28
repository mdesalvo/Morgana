using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Morgana.Contracts;

namespace PromptHarness.Infrastructure;

/// <summary>
/// Reads the two structural signals a turn leaves behind inside the host process: the
/// <c>morgana.agent</c> span and the context-tool log lines.
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

    /// <summary>Listener on the framework's single <see cref="ActivitySource"/>.</summary>
    private readonly ActivityListener listener;

    /// <summary>Closed <c>morgana.agent</c> spans, per conversation, in completion order.</summary>
    private readonly ConcurrentDictionary<string, List<AgentSpan>> agentSpans = new ConcurrentDictionary<string, List<AgentSpan>>();

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
        int llmMark;
        lock (llmGate)
            llmMark = llmSpans.Count;

        return new TurnScope(
            conversationId,
            output.Mark(),
            agentSpans.TryGetValue(conversationId, out List<AgentSpan>? spans) ? spans.Count : 0,
            llmMark);
    }

    /// <summary>Closes the window and assembles what was observed alongside the delivered message.</summary>
    public async Task<TurnResult> CompleteTurnAsync(TurnScope scope, string userMessage, ChannelMessage message)
    {
        // The console logger writes on a background queue, so the last tool lines of a turn can
        // still be in flight when the webhook has already been delivered.
        await Task.Delay(logDrainMilliseconds);

        IReadOnlyList<string> lines = output.Since(scope.LogMark);

        List<ContextAccess> accesses = [];
        foreach (string line in lines)
        {
            Match match = ContextAccessPattern.Match(line);
            if (!match.Success)
                continue;

            ContextOperation operation = match.Groups["op"].Value switch
            {
                "HIT" => ContextOperation.Hit,
                "MISS" => ContextOperation.Miss,
                _ => ContextOperation.Set
            };

            accesses.Add(new ContextAccess(operation, match.Groups["name"].Value));
        }

        AgentSpan? span = agentSpans.TryGetValue(scope.ConversationId, out List<AgentSpan>? spans) && spans.Count > scope.SpanCount
            ? spans[^1]
            : null;

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
            lines);
    }

    /// <inheritdoc />
    public void Dispose() => listener.Dispose();

    /// <summary>Records a closed agent span against its conversation, or an LLM span's token usage.</summary>
    private void OnActivityStopped(Activity activity)
    {
        if (activity.Source.Name == "Morgana.AI.LLM")
        {
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

        if (activity.OperationName != "morgana.agent")
            return;

        string? conversationId = activity.GetTagItem("conversation.id") as string;
        if (conversationId is null)
            return;

        string toolsInvoked = activity.GetTagItem("agent.tools_invoked") as string ?? string.Empty;

        AgentSpan span = new AgentSpan(
            activity.GetTagItem("agent.name") as string,
            [.. toolsInvoked.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]);

        agentSpans.AddOrUpdate(conversationId, _ => [span], (_, existing) =>
        {
            lock (existing)
                existing.Add(span);

            return existing;
        });
    }

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

    /// <summary>Compact rendering for transcripts and baseline files.</summary>
    public override string ToString()
        => $"{Calls} call(s), in={InputTokens}, out={OutputTokens}, cacheRead={CacheReadTokens}, cacheWrite={CacheWriteTokens}";
}

/// <summary>
/// Marks the start of a turn's observation window: where the log stood, and how many agent spans
/// the conversation had already produced.
/// </summary>
/// <param name="ConversationId">Conversation being observed.</param>
/// <param name="LogMark">Index into the captured log at the moment the turn was sent.</param>
/// <param name="SpanCount">Agent spans already recorded for the conversation.</param>
/// <param name="LlmSpanCount">LLM spans already recorded, process-wide.</param>
public sealed record TurnScope(string ConversationId, int LogMark, int SpanCount, int LlmSpanCount);
