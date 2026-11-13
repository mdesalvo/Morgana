using System.Globalization;
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
    /// The variable was handed to the model in the per-turn <c>HeldContextDeclaration</c>, so no <c>GetContextVariable</c> call was needed.
    /// It is the proof of hydration for a held variable, which a follow-up agent reads from the declaration rather than from a tool call.
    /// </summary>
    Declared
}

/// <summary>A single context-variable access observed on a turn, in the order it happened.</summary>
/// <param name="Operation">Whether the access was a hit, a miss, a declaration or a write.</param>
/// <param name="VariableName">Name the agent used — the thing the closed-vocabulary assertions are about.</param>
public sealed record ContextAccess(ContextOperation Operation, string VariableName);

/// <summary>
/// One consultation served during a turn, read from the <c>morgana.consultation</c> span: the one window onto the other side of the exchange.
/// It shows which tools the colleague reached for and whether it ended waiting for something back, as a colleague that demanded a value instead of answering does.
/// </summary>
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

/// <summary>One tool result the framework withheld from the model, read from the event it adds to the span of the turn.</summary>
/// <param name="ToolName">The function the model called.</param>
/// <param name="Outcome">What happened to the call: a result withheld or a call that was never sent.</param>
/// <param name="Source">The layer that decided the withholding; null when the call was kept from running and nothing was judged.</param>
/// <param name="Finding">The guard's clinical finding, never the text it judged; null when no judgment was made.</param>
public sealed record WithheldToolObservation(string ToolName, string Outcome, string? Source, string? Finding);

/// <summary>One count of Morgana's security counter: what happened at a guarded door.</summary>
/// <param name="Door">Where the event happened.</param>
/// <param name="Outcome">What happened there.</param>
/// <param name="Source">The layer that decided; null when the event carries none.</param>
/// <param name="ErrorType">The technical cause when the door could not judge; null otherwise.</param>
public sealed record SecurityEventObservation(string Door, string Outcome, string? Source, string? ErrorType);

/// <summary>
/// One LLM call as the model saw it: its cost, its instructions, its tools, the messages it read and its answer.
/// The texts are the OpenTelemetry <c>gen_ai.*</c> content attributes as JSON, null when the host records no content.
/// </summary>
/// <param name="Usage">The tokens the call consumed.</param>
/// <param name="Instructions">The system instructions it was given.</param>
/// <param name="ToolDefinitions">The tools it was offered.</param>
/// <param name="InputMessages">The messages it read.</param>
/// <param name="OutputMessages">What it answered.</param>
public sealed record ModelCall(
    TokenUsage Usage,
    string? Instructions,
    string? ToolDefinitions,
    string? InputMessages,
    string? OutputMessages);

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
/// <param name="GuardSource">The layer that decided the verdict, from the same span; null when the guard rail is disabled or no guard span was seen.</param>
/// <param name="GuardErrorType">The technical cause from the same span when the guard could not judge; null otherwise.</param>
/// <param name="ClassifierIntent">Intent from the <c>morgana.classifier</c> span; null on a follow-up turn, which skips classification entirely.</param>
/// <param name="ClassifierConfidence">Confidence from the same span, parsed from its string tag.</param>
/// <param name="Consultations">Consultations served during the turn, in the order they closed; empty on a turn that asked no colleague.</param>
/// <param name="CumulativeLogLines">
/// Host log lines since conversation start, empty unless the caller passed a conversation-start mark into <c>TurnObserver.CompleteTurnAsync</c>.
/// A signal sent once, such as a dust threshold, is crossed on a turn that varies between runs, so it is asserted against the whole conversation.
/// </param>
/// <param name="ModelCalls">Every LLM call of the turn as the model saw it, classification included; the forensic record of a failing run.</param>
/// <param name="JudgeModelCalls">The judge's own calls on this turn, kept apart so that the turn's cost stays Morgana's alone.</param>
/// <param name="WithheldToolObservations">Tool results withheld during the turn, in the order they were; empty when none was.</param>
/// <param name="SecurityEventWindow">Security events counted during the turn.</param>
/// <param name="ProcessSecurityEvents">Every security event counted since the process started, which is where an offer refused at startup shows.</param>
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
    string? GuardSource = null,
    string? GuardErrorType = null,
    string? ClassifierIntent = null,
    double? ClassifierConfidence = null,
    IReadOnlyList<ConsultationObservation>? Consultations = null,
    IReadOnlyList<string>? CumulativeLogLines = null,
    IReadOnlyList<ModelCall>? ModelCalls = null,
    IReadOnlyList<ModelCall>? JudgeModelCalls = null,
    IReadOnlyList<WithheldToolObservation>? WithheldToolObservations = null,
    IReadOnlyList<SecurityEventObservation>? SecurityEventWindow = null,
    IReadOnlyList<SecurityEventObservation>? ProcessSecurityEvents = null)
{
    /// <summary>The host log lines since conversation start, never null; empty when the caller asked for no cumulative tracking.</summary>
    public IReadOnlyList<string> Cumulative => CumulativeLogLines ?? [];

    /// <summary>The turn's LLM calls as the model saw them, never null.</summary>
    public IReadOnlyList<ModelCall> Calls => ModelCalls ?? [];

    /// <summary>The judge's calls on the turn, never null; empty where the turn was not judged.</summary>
    public IReadOnlyList<ModelCall> JudgeCalls => JudgeModelCalls ?? [];

    /// <summary>Tool results withheld during the turn, never null.</summary>
    public IReadOnlyList<WithheldToolObservation> WithheldTools => WithheldToolObservations ?? [];

    /// <summary>Security events counted during the turn, never null.</summary>
    public IReadOnlyList<SecurityEventObservation> TurnSecurityEvents => SecurityEventWindow ?? [];

    /// <summary>Every security event counted since the process started, never null.</summary>
    public IReadOnlyList<SecurityEventObservation> AllSecurityEvents => ProcessSecurityEvents ?? [];

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
                // A fail or crit entry opens an error.
                if (line.StartsWith("fail:", StringComparison.Ordinal) || line.StartsWith("crit:", StringComparison.Ordinal))
                    insideError = true;

                // Any other unindented line opens an entry of a lower level, which ends the error.
                else if (line.Length > 0 && !char.IsWhiteSpace(line[0]))
                    insideError = false;

                // The error's own lines, its exception included, are kept.
                if (insideError)
                    errors.Add(line);
            }

            // Every error entry of the turn, which Describe appends to a failing turn.
            return errors;
        }
    }

    /// <summary>Names read, by a hit, a miss or the held-context declaration.</summary>
    public IReadOnlyList<string> ContextReads
        => [.. ContextAccesses.Where(a => a.Operation != ContextOperation.Set).Select(a => a.VariableName)];

    /// <summary>Names written via <c>SetContextVariable</c>.</summary>
    public IReadOnlyList<string> ContextWrites
        => [.. ContextAccesses.Where(a => a.Operation == ContextOperation.Set).Select(a => a.VariableName)];

    /// <summary>Text of the delivered message, never null.</summary>
    public string Text => Message.Text ?? string.Empty;

    /// <summary>Quick replies delivered with the message, empty when none.</summary>
    public IReadOnlyList<QuickReply> QuickReplies => Message.QuickReplies ?? [];

    /// <summary>
    /// A compact rendering of the turn, used in assertion failure messages.
    /// It carries one line per structural signal, so a failure shows everything ExpectationChecker could check and not only the property that failed.
    /// </summary>
    public string Describe()
        => $"""
            user: {UserMessage}
            {(GuardCompliant is null ? "" : $"guard: compliant={GuardCompliant} | source={GuardSource ?? "(none)"} | violation={GuardViolation ?? "(none)"}\n            ")}{(ClassifierIntent is null ? "" : $"classifier: intent={ClassifierIntent} | confidence={ClassifierConfidence?.ToString("F2", CultureInfo.InvariantCulture) ?? "(unknown)"}\n            ")}agent: {AgentName ?? "(no agent span)"} | completed={Message.AgentCompleted} | quickReplies={QuickReplies.Count} | richCard={(Message.RichCard is null ? "absent" : "present")}
            tools: {(ToolsInvoked.Count == 0 ? "(none)" : string.Join(", ", ToolsInvoked))}
            {(Consultations is not { Count: > 0 } ? "" : string.Join("\n            ", Consultations.Select(c => $"consulted {c.Target}: tools={(c.ToolsInvoked.Count == 0 ? "(none)" : string.Join("/", c.ToolsInvoked))} | awaitingReply={c.AwaitingReply}\n              asked: {c.Question}\n              replied: {c.Answer}")) + "\n            ")}
            {(WithheldTools.Count == 0 ? "" : string.Join("\n            ", WithheldTools.Select(w => $"withheld: tool={w.ToolName} | outcome={w.Outcome} | source={w.Source ?? "(none)"} | finding={w.Finding ?? "(none)"}")) + "\n            ")}{(TurnSecurityEvents.Count == 0 ? "" : string.Join("\n            ", TurnSecurityEvents.Select(e => $"security: door={e.Door} | outcome={e.Outcome} | source={e.Source ?? "(none)"}")) + "\n            ")}tokens: {Tokens}
            context: {(ContextAccesses.Count == 0 ? "(none)" : string.Join(", ", ContextAccesses.Select(a => $"{a.Operation}:{a.VariableName}")))}
            text: {Text}{(HostErrors.Count == 0 ? "" : "\nhost errors:\n" + string.Join("\n", HostErrors))}
            """;
}