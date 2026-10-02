using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
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
/// One tool result the guard screened during a turn, read from the <c>morgana.toolguard</c> span.
/// </summary>
/// <param name="Tool">Function whose result was screened.</param>
/// <param name="External">Whether the result came from outside the installation and was read by the inspector.</param>
/// <param name="Compliant">Whether the result reached the model; false means it was quarantined.</param>
/// <param name="Source">Which layer decided, as the framework names it: <c>Prefilter</c>, <c>Inspector</c>, <c>ProviderFilter</c> or <c>FailOpen</c>.</param>
/// <param name="Violation">Why the result was quarantined, never a quote of it; null when it was admitted.</param>
public sealed record ToolGuardObservation(string? Tool, bool? External, bool? Compliant, string? Source = null, string? Violation = null);

/// <summary>
/// One question a partner put to an agent here, as the peer guard judged it on the <c>morgana.peerguard</c> span.
/// </summary>
/// <param name="Caller">Partner that asked.</param>
/// <param name="Target">Intent of the agent asked.</param>
/// <param name="Compliant">Whether the question reached the agent; false means it was declined.</param>
/// <param name="Source">Which layer decided, as the framework names it.</param>
/// <param name="Violation">Why the question was declined, never a quote of it; null when it reached the agent.</param>
public sealed record PeerGuardObservation(string? Caller, string? Target, bool? Compliant, string? Source, string? Violation);

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
/// <param name="ToolGuards">Tool results the guard screened during the turn, in the order they closed; empty when the tool guard is off.</param>
/// <param name="GuardSource">Which layer decided the user guard's verdict; null when no guard span was seen.</param>
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
    IReadOnlyList<ToolGuardObservation>? ToolGuards = null,
    string? GuardSource = null)
{
    /// <summary>How a turn's evidence is written for the judge: readable, with names as the framework spells them.</summary>
    private static readonly JsonSerializerOptions EvidenceFormat = new JsonSerializerOptions
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// What happened behind the scenes on this turn, read from the framework's own telemetry and
    /// written for the judge: names, verdicts and who decided them, never what a tool returned.
    /// </summary>
    /// <remarks>
    /// The judge does not share the user's purpose. A bug can sit behind the most innocuous answer:
    /// a source called twice, a result admitted unread, a turn left open. It is this record that lets
    /// the prose be judged against what actually happened.
    /// </remarks>
    public string Evidence()
        => JsonSerializer.Serialize(new
        {
            agent = AgentName,
            turnLeftOpenAwaitingTheUser = Message.AgentCompleted == false,
            classifier = ClassifierIntent is null ? null : new { intent = ClassifierIntent, confidence = ClassifierConfidence },
            userGuard = GuardCompliant is null ? null : new { compliant = GuardCompliant, decidedBy = GuardSource, violation = GuardViolation },
            toolsInvokedInOrder = ToolsInvoked,
            toolResultsScreened = Screened.Select(screened => new
            {
                tool = screened.Tool,
                externalSource = screened.External,
                verdict = screened.Compliant == false ? "quarantined" : "admitted",
                decidedBy = screened.Source,
                violation = screened.Violation
            }),
            consultations = Consulted.Select(consultation => new
            {
                colleague = consultation.Target,
                question = consultation.Question,
                answer = consultation.Answer,
                colleagueToolsInvoked = consultation.ToolsInvoked,
                colleagueAwaitsReply = consultation.AwaitingReply
            }),
            contextAccesses = ContextAccesses.Select(access => $"{access.Operation}:{access.VariableName}"),
            quickReplyCount = QuickReplies.Count,
            richCardShown = Message.RichCard is not null
        }, EvidenceFormat);

    /// <summary>Tool results screened during the turn, never null.</summary>
    public IReadOnlyList<ToolGuardObservation> Screened => ToolGuards ?? [];

    /// <summary>Consultations served during the turn, never null.</summary>
    public IReadOnlyList<ConsultationObservation> Consulted => Consultations ?? [];

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
            {(GuardCompliant is null ? "" : $"guard: compliant={GuardCompliant} | violation={GuardViolation ?? "(none)"}\n            ")}{(ClassifierIntent is null ? "" : $"classifier: intent={ClassifierIntent} | confidence={ClassifierConfidence?.ToString("F2", CultureInfo.InvariantCulture) ?? "(unknown)"}\n            ")}agent: {AgentName ?? "(no agent span)"} | completed={Message.AgentCompleted} | quickReplies={QuickReplies.Count} | richCard={(Message.RichCard is null ? "absent" : "present")}
            tools: {(ToolsInvoked.Count == 0 ? "(none)" : string.Join(", ", ToolsInvoked))}
            {(Consultations is not { Count: > 0 } ? "" : string.Join("\n            ", Consultations.Select(c => $"consulted {c.Target}: tools={(c.ToolsInvoked.Count == 0 ? "(none)" : string.Join("/", c.ToolsInvoked))} | awaitingReply={c.AwaitingReply}\n              asked: {c.Question}\n              replied: {c.Answer}")) + "\n            ")}
            {(ToolGuards is not { Count: > 0 } ? "" : "screened: " + string.Join(", ", ToolGuards.Select(g => $"{g.Tool}={(g.Compliant == false ? "quarantined" : "admitted")} by {g.Source ?? "?"}{(g.External == true ? " (external)" : "")}")) + "\n            ")}tokens: {Tokens}
            context: {(ContextAccesses.Count == 0 ? "(none)" : string.Join(", ", ContextAccesses.Select(a => $"{a.Operation}:{a.VariableName}")))}
            text: {Text}
            """;
}
