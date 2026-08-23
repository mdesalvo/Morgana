using Morgana.Contracts;

namespace PromptHarness.Infrastructure.Wiring;

/// <summary>How a turn touched one context variable.</summary>
public enum ContextOperation
{
    /// <summary><c>GetContextVariable</c> found the variable.</summary>
    Hit,

    /// <summary><c>GetContextVariable</c> did not find the variable.</summary>
    Miss,

    /// <summary><c>SetContextVariable</c> wrote the variable.</summary>
    Set,

    /// <summary>
    /// The variable was handed to the model directly in the per-turn <c>HeldContextDeclaration</c>
    /// injection (name and value), so no <c>GetContextVariable</c> call happened — there was nothing
    /// left to look up. The proof of hydration for an already-held variable, replacing <c>Hit</c> in
    /// that case: a follow-up agent, or a later turn of the same agent, reads its held variables from
    /// the declaration, not from a tool call.
    /// </summary>
    Declared
}

/// <summary>A single context-variable access observed on a turn, in the order it happened.</summary>
/// <param name="Operation">Whether the access was a hit, a miss or a write.</param>
/// <param name="VariableName">Name the agent used — the thing the closed-vocabulary assertions are about.</param>
public sealed record ContextAccess(ContextOperation Operation, string VariableName);

/// <summary>
/// Everything the harness observed about one turn: what the user said, what the channel received,
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
/// <param name="CumulativeLogLines">
/// Host log lines since conversation start, not just since this turn began — empty unless the
/// caller passed a conversation-start mark into <c>TurnObserver.CompleteTurnAsync</c>. What a
/// one-shot, edge-triggered signal (a dust-budget threshold, never resent once crossed) needs: which
/// exact turn crosses it is a token-cost measurement that can shift by a turn or two between runs, so
/// checking "has this happened by now" against the whole conversation is the only way to assert on it
/// without pinning to a turn index that variance can invalidate. See <c>ExpectationChecker.CheckDust</c>.
/// </param>
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
    IReadOnlyList<string>? CumulativeLogLines = null)
{
    /// <summary>Never null even when the caller didn't ask for cumulative tracking — see the parameter's own remarks.</summary>
    public IReadOnlyList<string> Cumulative => CumulativeLogLines ?? [];

    /// <summary>Names read via <c>GetContextVariable</c>, whether the read hit or missed.</summary>
    public IReadOnlyList<string> ContextReads
        => [.. ContextAccesses.Where(a => a.Operation != ContextOperation.Set).Select(a => a.VariableName)];

    /// <summary>Names written via <c>SetContextVariable</c>.</summary>
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
        => $"""
            user: {UserMessage}
            {(GuardCompliant is null ? "" : $"guard: compliant={GuardCompliant} | violation={GuardViolation ?? "(none)"}\n            ")}{(ClassifierIntent is null ? "" : $"classifier: intent={ClassifierIntent} | confidence={ClassifierConfidence?.ToString("F2") ?? "(unknown)"}\n            ")}agent: {AgentName ?? "(no agent span)"} | completed={Message.AgentCompleted} | quickReplies={QuickReplies.Count} | richCard={(Message.RichCard is null ? "absent" : "present")}
            tools: {(ToolsInvoked.Count == 0 ? "(none)" : string.Join(", ", ToolsInvoked))}
            tokens: {Tokens}
            context: {(ContextAccesses.Count == 0 ? "(none)" : string.Join(", ", ContextAccesses.Select(a => $"{a.Operation}:{a.VariableName}")))}
            text: {Text}
            """;
}
