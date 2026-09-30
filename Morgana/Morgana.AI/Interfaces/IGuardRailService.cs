namespace Morgana.AI.Interfaces;

/// <summary>
/// Service abstraction for policy enforcement on the text that reaches a model: the user's message
/// before the pipeline runs and a tool's result before the agent reads it.
/// Decouples guard-rail logic from the actor infrastructure and from the agent's tool loop, which
/// both delegate entirely to this service and are agnostic of the underlying implementation strategy.
/// Default implementation: LLMGuardRailService provides LLM-based policy evaluation.
/// Fail-safe contract: on transient errors returns a compliant result rather than blocking legitimate traffic.
/// </summary>
public interface IGuardRailService
{
    /// <summary>
    /// Evaluates whether the user's message complies with content and policy rules.
    /// </summary>
    /// <param name="conversationId">
    /// Unique identifier of the ongoing conversation.
    /// Passed for correlation/logging purposes; implementations may use it to apply
    /// per-conversation policies or to enrich audit trails.
    /// </param>
    /// <param name="message">User message text to evaluate.</param>
    /// <returns>
    /// A <see cref="Records.GuardRailResult"/> indicating whether the message is compliant
    /// and, when not, describing the violated rule.
    /// </returns>
    Task<Records.GuardRailResult> CheckUserMessageAsync(string conversationId, string message);

    /// <summary>
    /// Evaluates whether a tool's result addresses the assistant instead of carrying data, before the
    /// model reads it.
    /// </summary>
    /// <param name="conversationId">Conversation the tool ran on, for correlation and charging.</param>
    /// <param name="toolName">Function the model called, named in the diagnostics.</param>
    /// <param name="toolResult">The result as the model would read it.</param>
    /// <param name="externalSource">
    /// True when the result comes from outside this installation (an MCP server or a partner's
    /// agent): nobody here vouches for its text, so it warrants the deeper check.
    /// </param>
    /// <returns>
    /// Compliant when the result may reach the model; otherwise the reason, meant for the log alone.
    /// </returns>
    Task<Records.GuardRailResult> CheckToolResultAsync(string conversationId, string toolName, string toolResult, bool externalSource);
}