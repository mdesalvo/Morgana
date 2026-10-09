using System.Globalization;
using Morgana.Contracts;

namespace PromptHarness.Infrastructure.Wiring;

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
        => $"""
            user: {UserMessage}
            {(GuardCompliant is null ? "" : $"guard: compliant={GuardCompliant} | violation={GuardViolation ?? "(none)"}\n            ")}{(ClassifierIntent is null ? "" : $"classifier: intent={ClassifierIntent} | confidence={ClassifierConfidence?.ToString("F2", CultureInfo.InvariantCulture) ?? "(unknown)"}\n            ")}agent: {AgentName ?? "(no agent span)"} | completed={Message.AgentCompleted} | quickReplies={QuickReplies.Count} | richCard={(Message.RichCard is null ? "absent" : "present")}
            tools: {(ToolsInvoked.Count == 0 ? "(none)" : string.Join(", ", ToolsInvoked))}
            {(Consultations is not { Count: > 0 } ? "" : string.Join("\n            ", Consultations.Select(c => $"consulted {c.Target}: tools={(c.ToolsInvoked.Count == 0 ? "(none)" : string.Join("/", c.ToolsInvoked))} | awaitingReply={c.AwaitingReply}\n              asked: {c.Question}\n              replied: {c.Answer}")) + "\n            ")}
            tokens: {Tokens}
            context: {(ContextAccesses.Count == 0 ? "(none)" : string.Join(", ", ContextAccesses.Select(a => $"{a.Operation}:{a.VariableName}")))}
            text: {Text}{(HostErrors.Count == 0 ? "" : "\nhost errors:\n" + string.Join("\n", HostErrors))}
            """;
}
