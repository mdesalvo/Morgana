namespace Morgana.AI.Interfaces;

/// <summary>
/// Service abstraction for intent classification of user messages.
/// Implementations can range from LLM-based classification to rule engines,
/// ML models, or external classification APIs.
/// </summary>
/// <remarks>
/// <para><strong>Design Intent:</strong></para>
/// <para>Decouples classification logic from the actor infrastructure.
/// <see cref="Actors.ClassifierActor"/> delegates entirely to this service and is agnostic
/// of the underlying classification strategy.</para>
///
/// <para><strong>Default Implementation:</strong></para>
/// <para><see cref="Services.LLMClassifierService"/> provides LLM-based intent classification
/// using the configured intents from <c>agents.json</c> and the Classifier prompt from
/// <c>morgana.json</c>. Swap it in DI to adopt any alternative classification backend
/// without touching the actor system.</para>
///
/// <para><strong>Fail-Safe Contract:</strong></para>
/// <para>Implementations should never throw on classification failure. Instead, they must
/// return a result with intent <c>"other"</c> and confidence <c>0.0</c> so the conversation
/// can continue gracefully. Throwing exceptions is acceptable only for non-transient
/// configuration failures at startup.</para>
/// </remarks>
public interface IClassifierService
{
    /// <summary>
    /// Classifies the given user message and returns the detected intent with metadata.
    /// </summary>
    /// <param name="conversationId">
    /// Unique identifier of the ongoing conversation.
    /// Passed for correlation and logging purposes.
    /// </param>
    /// <param name="message">User message text to classify.</param>
    /// <returns>
    /// A <see cref="Records.ClassificationResult"/> containing the detected intent name
    /// and a metadata dictionary (at minimum a <c>"confidence"</c> key).
    /// On failure, implementations must return a fallback result with intent <c>"other"</c>.
    /// </returns>
    Task<Records.ClassificationResult> ClassifyAsync(string conversationId, string message);
}