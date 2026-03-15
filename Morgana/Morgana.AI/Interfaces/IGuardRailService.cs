namespace Morgana.AI.Interfaces;

/// <summary>
/// Service abstraction for content moderation and policy enforcement on user messages.
/// Implementations can range from simple LLM-based checks to enterprise-grade solutions
/// (e.g. Microsoft Purview Content Moderator, Azure AI Content Safety, custom rule engines).
/// </summary>
/// <remarks>
/// <para><strong>Design Intent:</strong></para>
/// <para>Decouples guard-rail logic from the actor infrastructure. <see cref="Actors.GuardActor"/>
/// delegates entirely to this service and is agnostic of the underlying implementation strategy.</para>
///
/// <para><strong>Default Implementation:</strong></para>
/// <para><see cref="Services.LLMGuardRailService"/> provides a two-level check:
/// a fast synchronous profanity filter followed by an asynchronous LLM-based policy evaluation.
/// Swap it in DI to adopt any alternative moderation backend without touching the actor system.</para>
///
/// <para><strong>Fail-Open Contract:</strong></para>
/// <para>Implementations are expected to fail open on transient errors (i.e. return a compliant result
/// rather than blocking legitimate traffic). If the service is unable to evaluate the message,
/// it should log the error and return <c>Compliant = true</c> so the conversation can continue.
/// Throwing exceptions is acceptable for non-transient configuration failures at startup.</para>
/// </remarks>
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
    /// <remarks>
    /// <para>This method is called for <strong>every</strong> user message — both new requests and
    /// follow-up messages to active agents — so implementations should be designed for low latency
    /// on the happy path (compliant messages).</para>
    /// </remarks>
    Task<Records.GuardRailResult> CheckAsync(string conversationId, string message);
}