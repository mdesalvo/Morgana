namespace Morgana.AI.Interfaces;

/// <summary>
/// Decides whether a user message complies with the content and policy rules.
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