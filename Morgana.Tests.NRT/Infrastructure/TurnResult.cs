using Morgana.Contracts;

namespace Morgana.Tests.NRT.Infrastructure;

/// <summary>How a turn touched one context variable.</summary>
public enum ContextOperation
{
    /// <summary><c>GetContextVariable</c> found the variable.</summary>
    Hit,

    /// <summary><c>GetContextVariable</c> did not find the variable.</summary>
    Miss,

    /// <summary><c>SetContextVariable</c> wrote the variable.</summary>
    Set
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
public sealed record TurnResult(
    string ConversationId,
    string UserMessage,
    ChannelMessage Message,
    IReadOnlyList<string> ToolsInvoked,
    IReadOnlyList<ContextAccess> ContextAccesses,
    string? AgentName,
    TokenUsage Tokens,
    IReadOnlyList<string> LogLines)
{
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
    public string Describe()
        => $"""
            user: {UserMessage}
            agent: {AgentName ?? "(no agent span)"} | completed={Message.AgentCompleted} | quickReplies={QuickReplies.Count} | richCard={(Message.RichCard is null ? "absent" : "present")}
            tools: {(ToolsInvoked.Count == 0 ? "(none)" : string.Join(", ", ToolsInvoked))}
            tokens: {Tokens}
            context: {(ContextAccesses.Count == 0 ? "(none)" : string.Join(", ", ContextAccesses.Select(a => $"{a.Operation}:{a.VariableName}")))}
            text: {Text}
            """;
}
