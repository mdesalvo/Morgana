using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Morgana.Contracts;

namespace Morgana.Tests.NRT.Infrastructure;

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
            ShouldListenTo = source => source.Name == "Morgana",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = OnActivityStopped
        };

        ActivitySource.AddActivityListener(listener);
    }

    /// <summary>Opens an observation window for a turn about to be sent.</summary>
    public TurnScope BeginTurn(string conversationId)
        => new TurnScope(
            conversationId,
            output.Mark(),
            agentSpans.TryGetValue(conversationId, out List<AgentSpan>? spans) ? spans.Count : 0);

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

        return new TurnResult(
            scope.ConversationId,
            userMessage,
            message,
            span?.ToolsInvoked ?? [],
            accesses,
            span?.AgentName,
            lines);
    }

    /// <inheritdoc />
    public void Dispose() => listener.Dispose();

    /// <summary>Records a closed agent span against its conversation.</summary>
    private void OnActivityStopped(Activity activity)
    {
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

    /// <summary>What a closed <c>morgana.agent</c> span contributes to a turn result.</summary>
    private sealed record AgentSpan(string? AgentName, IReadOnlyList<string> ToolsInvoked);
}

/// <summary>
/// Marks the start of a turn's observation window: where the log stood, and how many agent spans
/// the conversation had already produced.
/// </summary>
/// <param name="ConversationId">Conversation being observed.</param>
/// <param name="LogMark">Index into the captured log at the moment the turn was sent.</param>
/// <param name="SpanCount">Agent spans already recorded for the conversation.</param>
public sealed record TurnScope(string ConversationId, int LogMark, int SpanCount);
