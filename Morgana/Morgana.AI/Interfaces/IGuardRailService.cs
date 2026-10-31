namespace Morgana.AI.Interfaces;

/// <summary>
/// Service abstraction for content moderation and policy enforcement on what reaches Morgana's models: the user's message and the results of the tools her agents call.
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
    Task<Records.GuardRailResult> CheckUserMessageAsync(string conversationId, string message);

    /// <summary>
    /// Judges a tool result before the agent's model reads it.
    /// </summary>
    /// <param name="conversationId">Passed for correlation and logging.</param>
    /// <param name="toolName">The function the model called.</param>
    /// <param name="toolResult">The result serialized as JSON, as the tool loop hands it to the provider.</param>
    /// <returns>
    /// Non-compliant when the result is to be withheld, with the finding in <c>Violation</c> for an operator's log.
    /// </returns>
    /// <remarks>Never throws: an evaluation that fails admits the result.</remarks>
    Task<Records.GuardRailResult> CheckToolResultAsync(string conversationId, string toolName, string toolResult);
}