namespace Morgana.AI.Interfaces;

/// <summary>
/// Service abstraction for content moderation and policy enforcement on user messages.
/// Decouples guard-rail logic from the actor infrastructure. GuardActor delegates entirely
/// to this service and is agnostic of the underlying implementation strategy.
/// Default implementation: LLMGuardRailService provides LLM-based policy evaluation.
/// Fail-safe contract: on transient errors returns a compliant result rather than blocking legitimate traffic.
/// </summary>
public interface IGuardRailService
{
    /// <summary>
    /// Evaluates whether the given message complies with content and policy rules.
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
    Task<Records.GuardRailResult> CheckAsync(string conversationId, string message);
}